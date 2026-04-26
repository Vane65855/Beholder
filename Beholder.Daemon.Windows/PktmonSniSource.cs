using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Beholder.Core;
using Beholder.Core.Tls;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon.Windows;

/// <summary>
/// Captures TLS ClientHello SNI extensions from outbound TCP/443 traffic via
/// the <c>Microsoft-Windows-PktMon</c> ETW provider, and feeds resolved
/// (hostname, destination IP) pairs into <see cref="IDnsCacheIngest"/>. This
/// closes the long-lived-connection hostname gap that DNS observation +
/// reverse-DNS fallback can't reach: a flow whose original DNS lookup has
/// aged out of every cache but whose TCP socket is still being reused for new
/// requests via HTTP/2 multiplexing or keep-alive. See ADR 006.
/// </summary>
/// <remarks>
/// <para>
/// Pktmon's ETW provider emits packet-level events with full payload when a
/// capture session is active. Each packet appears at multiple NDIS components
/// (~4 occurrences) under the same <c>PktGroupId</c>; we dedupe by group id
/// before parsing. The Phase 0 probe (commit history) confirmed the layout:
/// 32-byte metadata header, then the Ethernet frame, in event ID 160.
/// </para>
/// <para>
/// Hot-path discipline (<c>PRINCIPLES.md §Daemon Hot Path</c>): the ETW
/// callback does the minimum — extract <c>PktGroupId</c> + a managed-array
/// copy of the packet bytes — then pushes onto a bounded channel. The worker
/// thread does dedup, header parsing, SNI extraction, ingest, and backfill.
/// </para>
/// <para>
/// Failure modes (each logs at Warning and degrades to "no SNI source", the
/// daemon keeps running):
/// <list type="bullet">
///   <item><c>EnableSniCapture == false</c> → source skips entirely.</item>
///   <item><c>pktmon start</c> non-zero → can't enable provider, source skips.</item>
///   <item>TraceEvent session start fails → admin missing or session
///     conflict, source skips.</item>
///   <item>Channel full → drops oldest, increments <c>_droppedFromQueue</c>.
///     Periodic stats log shows the rate.</item>
///   <item>Backfill failure → caught at worker boundary, in-memory ingest
///     already succeeded, only persisted history misses out for that IP.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class PktmonSniSource
    : IHostedService, IAsyncDisposable, IDisposable {

    // Microsoft-Windows-PktMon. Confirmed via:
    //   logman query providers Microsoft-Windows-PktMon
    private static readonly Guid PktMonProvider = new("4D4F80D9-C8BD-4D73-BB5B-19C90402C5AC");
    private const string SessionName = "Beholder-SniCapture";

    // Pktmon event ID for a logged packet. Confirmed via Phase 0 probe.
    private const int PktMonEventIdPacket = 160;
    // Approximate offset where the post-metadata payload begins. Pktmon's
    // metadata is variable-length across events (additional fields appear
    // for some component/edge combinations) so we don't rely on a fixed
    // offset to find the Ethernet header — see FindIpv4EthernetStart for
    // the scan-based approach. Cutting from 32 here is safe because the
    // smallest plausible metadata is at least that long, so we never lose
    // packet bytes; we may include a few trailing metadata bytes which the
    // scan walks past.
    private const int PacketBytesCopyOffset = 32;
    private const int MinPacketLength = 14 + 20 + 20; // Ethernet + IPv4 + TCP minimums

    private readonly IDnsCacheIngest _ingest;
    private readonly IDnsHostnameBackfill _backfill;
    private readonly SniOptions _options;
    private readonly ILogger<PktmonSniSource> _logger;

    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(30);

    private TraceEventSession? _session;
    private Task? _drainTask;
    private Task? _consumerTask;
    private Channel<PacketSnapshot>? _queue;
    private CancellationTokenSource? _cts;
    private Timer? _statsTimer;
    private bool _started;
    private bool _disposed;

    // Stats — written from a single worker thread, read on stop, no
    // synchronization needed.
    private long _packetsObserved;
    private long _packetsAfterDedup;
    private long _tcp443Observed;
    private long _sniExtracted;
    private long _backfillFailures;
    private long _droppedFromQueue;

    public PktmonSniSource(
        IDnsCacheIngest ingest,
        IDnsHostnameBackfill backfill,
        IOptionsMonitor<SniOptions> options,
        ILogger<PktmonSniSource> logger
    ) {
        ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(backfill);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _ingest = ingest;
        _backfill = backfill;
        // Snapshot at construction. EnableSniCapture isn't hot-reloadable —
        // matches the EnableReverseDnsFallback contract on DnsOptions.
        _options = options.CurrentValue;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_options.EnableSniCapture) {
            _logger.LogInformation("SNI capture disabled by config");
            return Task.CompletedTask;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) {
            _logger.LogWarning("SNI capture skipped: requires Windows 10 22000+");
            return Task.CompletedTask;
        }

        // Stop any stale session left from a previous daemon instance.
        TryStopStaleSession(SessionName);

        // Pktmon must be running in capture mode for the ETW provider to emit
        // packet events. Reset first in case a previous (possibly hard-killed)
        // daemon left pktmon already in capture mode — `pktmon start` while
        // already running returns non-zero and would fail us out of the
        // happy path. The stop's exit code is ignored (it returns non-zero
        // when there's nothing to stop, which is also fine).
        TryRunPktmon("stop");
        // Pkt-size 0 = full packet (no truncation).
        var pktmonExit = TryRunPktmon("start --capture --pkt-size 0 --flags 0x010");
        if (pktmonExit != 0) {
            _logger.LogWarning(
                "SNI capture skipped: pktmon start exit code {Exit}; provider will not emit packet events",
                pktmonExit);
            return Task.CompletedTask;
        }

        try {
            _session = new TraceEventSession(SessionName) {
                BufferSizeMB = _options.SessionBufferSizeMB,
                StopOnDispose = true,
            };
            _session.EnableProvider(PktMonProvider, TraceEventLevel.Verbose, matchAnyKeywords: ulong.MaxValue);
            _session.Source.Dynamic.All += OnEtwEvent;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "SNI capture skipped: TraceEventSession setup failed");
            TryRunPktmon("stop");
            _session?.Dispose();
            _session = null;
            return Task.CompletedTask;
        }

        _queue = Channel.CreateBounded<PacketSnapshot>(new BoundedChannelOptions(_options.QueueCapacity) {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        var queue = _queue;
        var session = _session;

        _drainTask = Task.Run(() => {
            try { session.Source.Process(); }
            catch (Exception ex) when (!token.IsCancellationRequested) {
                _logger.LogError(ex, "PktMon ETW drain crashed");
            }
        }, CancellationToken.None);

        _consumerTask = Task.Run(() => ConsumerLoopAsync(queue, token), CancellationToken.None);

        // Periodic stats log so we have observability into SNI capture
        // health without needing a clean shutdown to flush the stop-line.
        // 30 s matches EtwDnsCache's metrics cadence.
        _statsTimer = new Timer(_ => LogStats("running"), null, StatsInterval, StatsInterval);

        _started = true;
        _logger.LogInformation(
            "SNI capture started (queue capacity {Capacity}, dedup capacity {Dedup}, session buffer {Buffer} MB)",
            _options.QueueCapacity, _options.DedupCapacity, _options.SessionBufferSizeMB);
        return Task.CompletedTask;
    }

    private void LogStats(string phase) {
        _logger.LogInformation(
            "SNI capture stats ({Phase}): observed={Observed}, deduped={Deduped}, tcp443={Tcp443}, sni={Sni}, backfillFailures={BackfillFails}, dropped={Dropped}",
            phase,
            Interlocked.Read(ref _packetsObserved),
            Interlocked.Read(ref _packetsAfterDedup),
            Interlocked.Read(ref _tcp443Observed),
            Interlocked.Read(ref _sniExtracted),
            Interlocked.Read(ref _backfillFailures),
            Interlocked.Read(ref _droppedFromQueue));
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        if (!_started) {
            // Either disabled, OS-incompatible, pktmon failed, or session-setup failed.
            return;
        }

        _statsTimer?.Dispose();
        _cts?.Cancel();
        try { _session?.Stop(noThrow: true); } catch { /* best-effort */ }
        _queue?.Writer.TryComplete();

        try {
            if (_consumerTask is not null) {
                await _consumerTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            if (_drainTask is not null) {
                await _drainTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // Expected during shutdown.
        } catch (TimeoutException) {
            _logger.LogWarning("SNI capture worker did not stop within 5 s; abandoning");
        }

        TryRunPktmon("stop");

        LogStats("stopped");
    }

    private void OnEtwEvent(TraceEvent ev) {
        // Hot path. Keep it tight — just extract PktGroupId + copy packet
        // bytes out of the unmanaged event buffer (its lifetime ends at this
        // callback's return), and push to the channel. Anything heavier
        // happens on the consumer thread.
        if (_queue is null) return;
        try {
            if ((int)ev.ID != PktMonEventIdPacket) return;
            var data = ev.EventData();
            if (data is null || data.Length <= PacketBytesCopyOffset + MinPacketLength) return;

            // PktGroupId is the first 8 bytes of the event-data buffer
            // (uint64 little-endian, per Phase 0 probe).
            var pktGroupId = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0, 8));

            // Copy packet bytes (Ethernet+IP+TCP+payload) out of the event
            // buffer. The slice is needed because the event-buffer reuse in
            // TraceEvent's source isn't compatible with deferred processing.
            var packetLen = data.Length - PacketBytesCopyOffset;
            var packetBytes = new byte[packetLen];
            Buffer.BlockCopy(data, PacketBytesCopyOffset, packetBytes, 0, packetLen);

            var snapshot = new PacketSnapshot(pktGroupId, packetBytes);
            if (!_queue.Writer.TryWrite(snapshot)) {
                Interlocked.Increment(ref _droppedFromQueue);
            }
        } catch {
            // Outer-boundary catch — the ETW drain thread must not crash.
            // Drop the event silently; the periodic stats log will surface
            // throughput regressions if this becomes a real problem.
        }
    }

    private async Task ConsumerLoopAsync(Channel<PacketSnapshot> queue, CancellationToken token) {
        var dedup = new LruIntegerSet(_options.DedupCapacity);
        try {
            await foreach (var snapshot in queue.Reader.ReadAllAsync(token).ConfigureAwait(false)) {
                Interlocked.Increment(ref _packetsObserved);

                // Deduplicate — each captured packet appears ~4 times at
                // different NDIS components, all sharing one PktGroupId.
                if (!dedup.TryAdd(snapshot.PktGroupId)) continue;
                Interlocked.Increment(ref _packetsAfterDedup);

                await ProcessPacketAsync(snapshot.PacketBytes, token).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // Shutdown.
        } catch (Exception ex) {
            _logger.LogError(ex, "SNI capture consumer loop crashed");
        }
    }

    private async Task ProcessPacketAsync(byte[] packetBytes, CancellationToken cancellationToken) {
        try {
            if (!TryExtractTlsContext(packetBytes, out var destIp, out var tcpPayload)) return;
            Interlocked.Increment(ref _tcp443Observed);
            if (!TlsClientHelloParser.TryExtractSni(tcpPayload, out var hostname) || hostname is null) return;

            Interlocked.Increment(ref _sniExtracted);

            _ingest.IngestResolved(hostname, destIp);

            try {
                await _backfill.BackfillHostnameAsync(destIp, hostname, cancellationToken)
                    .ConfigureAwait(false);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw; // shutdown
            } catch (Exception ex) {
                Interlocked.Increment(ref _backfillFailures);
                _logger.LogWarning(ex,
                    "SNI backfill failed for {Address} (hostname already in memory cache)",
                    destIp);
            }

            _logger.LogDebug("SNI captured {Address} -> {Hostname}", destIp, hostname);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Shutdown — propagate so the consumer loop exits cleanly.
            throw;
        } catch (Exception ex) {
            // Per-packet outer-boundary catch. The worker must not crash on
            // a single bad packet — log and move on.
            _logger.LogDebug(ex, "SNI processing skipped a packet");
        }
    }

    /// <summary>
    /// Locates the Ethernet frame within a Pktmon-prefixed packet buffer
    /// and walks Ethernet → IPv4 → TCP, validates outbound TCP/443
    /// (destination port 443), and slices out the TCP payload for the SNI
    /// parser. Returns <c>false</c> for IPv6, non-TCP, non-443, or any
    /// malformed framing.
    /// </summary>
    /// <remarks>
    /// Pktmon prepends variable-length metadata before the Ethernet frame —
    /// a fixed offset doesn't work across event subtypes. We scan a small
    /// window for the IPv4 ethertype signature (<c>0x08 0x00</c>) followed
    /// by a plausible IPv4 header start (version=4, IHL≥5, so byte
    /// <c>0x45..0x4F</c>). The first match wins.
    /// </remarks>
    private static bool TryExtractTlsContext(
        ReadOnlySpan<byte> packet,
        out IPAddress destIp,
        out ReadOnlySpan<byte> tcpPayload
    ) {
        destIp = IPAddress.None;
        tcpPayload = ReadOnlySpan<byte>.Empty;

        var ethStart = FindIpv4EthernetStart(packet);
        if (ethStart < 0) return false;

        var ethernet = packet[ethStart..];
        if (ethernet.Length < 14) return false;
        // Ethernet header: dst (6) + src (6) + ethertype (2). We don't
        // handle 802.1Q VLAN tags here — that's a follow-up if the daemon
        // ever runs on a VLAN-trunked interface.

        var ip = ethernet[14..];
        if (ip.Length < 20) return false;
        var version = ip[0] >> 4;
        if (version != 4) return false;
        var ihl = (ip[0] & 0x0F) * 4;
        if (ihl < 20 || ip.Length < ihl) return false;
        if (ip[9] != 0x06) return false; // protocol 6 = TCP

        var tcp = ip[ihl..];
        if (tcp.Length < 20) return false;
        var dstPort = (tcp[2] << 8) | tcp[3];
        if (dstPort != 443) return false;

        var tcpDataOffset = ((tcp[12] >> 4) & 0x0F) * 4;
        if (tcpDataOffset < 20 || tcp.Length < tcpDataOffset) return false;

        // ClientHello travels client → server, so the destination IP is the
        // server we're talking to — that's what we want to label.
        destIp = new IPAddress(ip.Slice(16, 4).ToArray());
        tcpPayload = tcp[tcpDataOffset..];
        return true;
    }

    /// <summary>
    /// Scans a small window at the start of the buffer for the Ethernet+IPv4
    /// signature: ethertype <c>0x0800</c> at offset 12 followed by an IPv4
    /// version+IHL byte at offset 14 (any value <c>0x45..0x4F</c>, meaning
    /// IPv4 with IHL≥5). Returns the offset where the Ethernet frame starts,
    /// or <c>-1</c> if no plausible signature is found within the scan window.
    /// </summary>
    private static int FindIpv4EthernetStart(ReadOnlySpan<byte> packet) {
        // Pktmon's per-event prefix is at most ~40 bytes; scan a bit beyond
        // that to be safe but bound the cost of the scan tightly.
        const int MaxScanOffset = 48;
        var limit = Math.Min(MaxScanOffset, packet.Length - 15);
        for (var offset = 0; offset <= limit; offset++) {
            if (packet[offset + 12] != 0x08) continue;
            if (packet[offset + 13] != 0x00) continue;
            var ipFirst = packet[offset + 14];
            // Version 4 (high nibble = 4) and IHL >= 5 (low nibble >= 5).
            if ((ipFirst & 0xF0) != 0x40) continue;
            if ((ipFirst & 0x0F) < 5) continue;
            return offset;
        }
        return -1;
    }

    private int TryRunPktmon(string args) {
        try {
            var psi = new ProcessStartInfo("pktmon", args) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return -1;
            p.WaitForExit(10_000);
            return p.ExitCode;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "pktmon {Args} invocation failed", args);
            return -1;
        }
    }

    private static void TryStopStaleSession(string name) {
        try {
            using var existing = TraceEventSession.GetActiveSession(name);
            existing?.Stop(noThrow: true);
        } catch {
            // No stale session — fine.
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _statsTimer?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        _session?.Dispose();
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        Dispose();
        GC.SuppressFinalize(this);
    }

    private readonly struct PacketSnapshot {
        public readonly ulong PktGroupId;
        public readonly byte[] PacketBytes;
        public PacketSnapshot(ulong pktGroupId, byte[] packetBytes) {
            PktGroupId = pktGroupId;
            PacketBytes = packetBytes;
        }
    }

    /// <summary>
    /// Bounded LRU set of integers. Single-threaded use only — accessed only
    /// from the consumer worker thread. Each <see cref="TryAdd"/> returns
    /// <c>true</c> if the value was newly added (caller should process the
    /// associated packet) or <c>false</c> if it was already present (skip).
    /// Capacity is enforced by evicting the oldest entry when full.
    /// </summary>
    private sealed class LruIntegerSet {
        private readonly int _capacity;
        private readonly HashSet<ulong> _set = new();
        private readonly Queue<ulong> _queue = new();

        public LruIntegerSet(int capacity) {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            _capacity = capacity;
        }

        public bool TryAdd(ulong value) {
            if (!_set.Add(value)) return false;
            _queue.Enqueue(value);
            if (_queue.Count > _capacity) {
                var oldest = _queue.Dequeue();
                _set.Remove(oldest);
            }
            return true;
        }
    }
}
