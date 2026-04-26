// Beholder.Tools.PktmonProbe — Phase 0 viability gate for SNI capture.
//
// Goal: empirically determine whether the Microsoft-Windows-PktMon ETW provider
// emits per-packet events with enough payload bytes to extract a TLS ClientHello
// + SNI extension on this build of Windows.
//
// The output of this probe drives a binary decision in the SNI-capture plan:
//   green → proceed to Phase 1 (TlsClientHelloParser)
//   red   → SNI capture is deferred, write ADR 006 (deferred variant), stop.
//
// Per PRINCIPLES.md §No Dead Weight, this project is committed to a probe branch
// and removed from master once the decision lands. Resurrectable later via
// `git checkout <hash> -- Beholder.Tools.PktmonProbe/` if a future Windows
// update demands re-investigation.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Beholder.Tools.PktmonProbe;

internal static class Program {
    // Microsoft-Windows-PktMon provider GUID, confirmed via:
    //   logman query providers Microsoft-Windows-PktMon
    private static readonly Guid PktMonProvider = new("4D4F80D9-C8BD-4D73-BB5B-19C90402C5AC");

    private const string SessionName = "Beholder-PktmonProbe";
    private const int RunSeconds = 30;

    // Stats counters (updated from the ETW callback thread; we read them on the
    // main thread after the run, no synchronization required because the writer
    // stops before we read).
    private static long _totalEvents;
    private static long _eventsWithPayload;
    private static long _eventsWithTlsRecord;
    private static long _eventsWithClientHello;
    private static long _eventsWithSni;
    private static long _tcp443Packets;
    private static readonly Dictionary<int, long> _eventIdCounts = new();
    private static readonly List<string> _sampleSnis = new();
    private static readonly object _sampleLock = new();
    private static readonly List<string> _tcp443Samples = new();
    private static readonly object _tcp443Lock = new();
    private const int Tcp443SamplesMax = 10;

    // Per-event-ID samples: first 3 events of each ID, with full EventData hex
    // dump + TraceEvent's interpretation. Helps us identify which event ID
    // carries packet payloads and at what offset within the event-data buffer.
    private static readonly Dictionary<int, List<string>> _eventSamples = new();
    private static readonly object _samplesLock = new();
    private const int SamplesPerEventId = 3;
    private const int SampleBytesPerEvent = 256;

    // Confirmed via probe iteration 2: event ID 160 carries packet bytes.
    // Layout: 32-byte metadata header (PktGroupId u64, PktNumber u32, then 8x
    // u16 fields, then 2x u16 size fields), then the Ethernet frame.
    private const int PktMonEventIdPacket = 160;
    private const int PacketBytesOffsetInEvent160 = 32;

    private static int Main(string[] args) {
        Console.WriteLine("Beholder PktmonProbe — Phase 0 viability gate for SNI capture");
        Console.WriteLine($"Run duration: {RunSeconds}s");
        Console.WriteLine($"Provider GUID: {PktMonProvider}");
        Console.WriteLine();

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) {
            Console.Error.WriteLine("This probe targets Windows 11 22H2+ only.");
            return 2;
        }

        if (!IsAdmin()) {
            Console.Error.WriteLine("Administrator required (TraceEventSession + pktmon start).");
            return 2;
        }

        // Best-effort: stop any stale Pktmon session. Ignore failures — pktmon
        // returns a non-zero exit code if no session is running, which is fine.
        TryRunPktmon("stop");
        TryStopStaleEtwSession(SessionName);

        // Start Pktmon capture so its ETW provider begins emitting.
        // --pkt-size 0 = capture full packet (no truncation, default is 128 bytes
        // which is enough for a small TLS ClientHello but might cut large ones).
        // --type all = flow + drop events (default).
        // --comp all = all components.
        // --flags 0x010 = raw packet (truncated to --pkt-size).
        var pktmonStart = TryRunPktmon("start --capture --pkt-size 0 --flags 0x010");
        if (pktmonStart != 0) {
            Console.Error.WriteLine($"pktmon start exit code {pktmonStart}; cannot proceed.");
            return 2;
        }

        try {
            using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableProvider(PktMonProvider, TraceEventLevel.Verbose, matchAnyKeywords: ulong.MaxValue);
            session.Source.Dynamic.All += OnEvent;

            using var processCts = new CancellationTokenSource();
            var processingTask = Task.Run(() => {
                try { session.Source.Process(); }
                catch (Exception ex) when (!processCts.IsCancellationRequested) {
                    Console.Error.WriteLine($"Processing exception: {ex.Message}");
                }
            });

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Capturing for {RunSeconds}s — open a fresh HTTPS site now.");
            for (var i = RunSeconds; i > 0; i--) {
                Console.Write($"\r[{DateTime.Now:HH:mm:ss}] {i,3}s remaining (events so far: {Interlocked.Read(ref _totalEvents)})   ");
                Thread.Sleep(1000);
            }
            Console.WriteLine();

            session.Stop();
            processCts.Cancel();
            processingTask.Wait(TimeSpan.FromSeconds(5));
        } finally {
            TryRunPktmon("stop");
        }

        PrintSummary();
        return 0;
    }

    private static void OnEvent(TraceEvent ev) {
        try {
            Interlocked.Increment(ref _totalEvents);

            // Aggregate event ID frequencies — the Pktmon manifest defines specific
            // event IDs for component registration, packet logging, etc. We're
            // probing to identify which IDs carry packet payloads.
            var id = (int)ev.ID;
            lock (_eventIdCounts) {
                _eventIdCounts[id] = _eventIdCounts.TryGetValue(id, out var count) ? count + 1 : 1;
            }

            // EventData() returns the raw user-data buffer the provider emitted.
            // For Pktmon's packet-logging events this should include the captured
            // packet bytes (Ethernet + IP + TCP + payload). We don't know the
            // exact event-data layout — Pktmon prefixes its own metadata before
            // the packet — so we scan the whole buffer for a TLS-record signature.
            var data = ev.EventData();
            if (data is null || data.Length == 0) return;
            Interlocked.Increment(ref _eventsWithPayload);

            // Capture first N events of each ID for post-run hex inspection.
            lock (_samplesLock) {
                if (!_eventSamples.TryGetValue(id, out var list)) {
                    list = new List<string>();
                    _eventSamples[id] = list;
                }
                if (list.Count < SamplesPerEventId) {
                    var take = Math.Min(SampleBytesPerEvent, data.Length);
                    var hex = Convert.ToHexString(data, 0, take);
                    var taskName = ev.TaskName ?? "<no-task>";
                    var opcode = ev.OpcodeName ?? "<no-opcode>";
                    var payloadNames = string.Join(",", ev.PayloadNames ?? Array.Empty<string>());
                    list.Add($"len={data.Length} task={taskName} opcode={opcode} payloadNames=[{payloadNames}] hex={hex}");
                }
            }

            // For event ID 160 specifically, parse the Ethernet+IP+TCP header
            // structure starting at the known offset, identify TCP/443 packets,
            // and capture diagnostic samples for those.
            if (id == PktMonEventIdPacket && data.Length > PacketBytesOffsetInEvent160 + 54) {
                TryInspectTcp443(data);
            }

            // Look for TLS handshake signature: 0x16 (handshake) + 0x03 0x0X (version)
            // followed by a plausible length. We scan the whole event payload because
            // the packet bytes are at an unknown offset within the event-specific
            // headers Pktmon prepends.
            for (var offset = 0; offset + 5 < data.Length; offset++) {
                if (data[offset] != 0x16) continue;          // ContentType.handshake
                if (data[offset + 1] != 0x03) continue;      // Major version
                var minor = data[offset + 2];
                if (minor > 0x04) continue;                   // 0x01..0x04 are TLS 1.0..1.3
                var recordLen = (data[offset + 3] << 8) | data[offset + 4];
                if (recordLen < 4 || recordLen > 16384) continue;
                // Looks like a TLS record header. Check that the handshake type
                // immediately after is ClientHello (0x01).
                if (offset + 5 >= data.Length) break;
                Interlocked.Increment(ref _eventsWithTlsRecord);
                if (data[offset + 5] != 0x01) break;          // Not ClientHello
                Interlocked.Increment(ref _eventsWithClientHello);

                // Try to find the SNI extension and pull the first hostname out.
                // We're approximate here on purpose — the production parser in
                // Phase 1 will be exhaustive; the probe just needs to confirm
                // payload sufficiency.
                var sni = TryExtractSniHeuristic(data, offset);
                if (sni is not null) {
                    Interlocked.Increment(ref _eventsWithSni);
                    lock (_sampleLock) {
                        if (_sampleSnis.Count < 10 && !_sampleSnis.Contains(sni)) {
                            _sampleSnis.Add(sni);
                        }
                    }
                }
                break;
            }
        } catch {
            // Outer-boundary catch — the probe must not crash on a single bad event.
        }
    }

    /// <summary>
    /// Parses the Ethernet + IPv4 + TCP headers in an event 160 payload.
    /// If it's TCP destination port 443, capture a diagnostic sample so we
    /// can manually verify whether HTTPS handshakes were observed and what
    /// their TLS-record bytes look like.
    /// </summary>
    private static void TryInspectTcp443(byte[] eventData) {
        var span = eventData.AsSpan(PacketBytesOffsetInEvent160);
        if (span.Length < 14) return;

        // Ethernet header: 6+6+2 bytes = 14. We assume EtherType is at 12-13.
        var etherType = (span[12] << 8) | span[13];
        if (etherType != 0x0800) return; // IPv4 only for now

        var ip = span[14..];
        if (ip.Length < 20) return;
        var ihl = (ip[0] & 0x0F) * 4;
        if (ihl < 20 || ip.Length < ihl) return;
        if (ip[9] != 0x06) return; // protocol 6 = TCP

        var tcp = ip[ihl..];
        if (tcp.Length < 20) return;
        var srcPort = (tcp[0] << 8) | tcp[1];
        var dstPort = (tcp[2] << 8) | tcp[3];
        if (dstPort != 443 && srcPort != 443) return;

        Interlocked.Increment(ref _tcp443Packets);

        // Capture the full TCP payload so we can inspect TLS records by hand.
        var tcpDataOffset = ((tcp[12] >> 4) & 0x0F) * 4;
        if (tcp.Length < tcpDataOffset) return;
        var tcpPayload = tcp[tcpDataOffset..];
        var srcIp = $"{ip[12]}.{ip[13]}.{ip[14]}.{ip[15]}";
        var dstIp = $"{ip[16]}.{ip[17]}.{ip[18]}.{ip[19]}";

        lock (_tcp443Lock) {
            if (_tcp443Samples.Count < Tcp443SamplesMax) {
                var hex = Convert.ToHexString(tcpPayload[..Math.Min(96, tcpPayload.Length)]);
                _tcp443Samples.Add($"{srcIp}:{srcPort} -> {dstIp}:{dstPort}, payload={tcpPayload.Length}B, first96={hex}");
            }
        }
    }

    /// <summary>
    /// Heuristic SNI extraction. Walks the ClientHello forward from the record
    /// start, skipping the random + sessionId + cipher suites + compression
    /// methods, and reads the first server_name extension's first hostname.
    /// Approximate — a full parser is Phase 1.
    /// </summary>
    private static string? TryExtractSniHeuristic(ReadOnlySpan<byte> data, int recordOffset) {
        // recordOffset points at the TLS record header (0x16 ...).
        // Skip 5 (record header) + 4 (handshake header: type + 24-bit length)
        // + 2 (legacy_version) + 32 (random) = 43 bytes.
        var p = recordOffset + 43;
        if (p >= data.Length) return null;

        // session_id: 1-byte length, then bytes.
        var sessionIdLen = data[p];
        p += 1 + sessionIdLen;
        if (p + 2 > data.Length) return null;

        // cipher_suites: 2-byte length, then bytes.
        var cipherLen = (data[p] << 8) | data[p + 1];
        p += 2 + cipherLen;
        if (p + 1 > data.Length) return null;

        // compression_methods: 1-byte length, then bytes.
        var compLen = data[p];
        p += 1 + compLen;
        if (p + 2 > data.Length) return null;

        // extensions: 2-byte total length, then list.
        var extTotalLen = (data[p] << 8) | data[p + 1];
        p += 2;
        var extEnd = p + extTotalLen;
        if (extEnd > data.Length) extEnd = data.Length;

        while (p + 4 <= extEnd) {
            var extType = (data[p] << 8) | data[p + 1];
            var extLen = (data[p + 2] << 8) | data[p + 3];
            p += 4;
            if (p + extLen > extEnd) return null;

            if (extType == 0) {
                // server_name extension. Layout:
                //   server_name_list (2-byte length)
                //     ServerName entries:
                //       NameType (1 byte; 0 = host_name)
                //       HostName (2-byte length, then ASCII bytes)
                if (extLen < 5) return null;
                var listLen = (data[p] << 8) | data[p + 1];
                if (listLen < 3 || p + 2 + listLen > p + extLen) return null;
                var entryStart = p + 2;
                if (data[entryStart] != 0) return null; // not host_name
                var hostLen = (data[entryStart + 1] << 8) | data[entryStart + 2];
                if (hostLen <= 0 || entryStart + 3 + hostLen > p + extLen) return null;
                var bytes = data.Slice(entryStart + 3, hostLen);
                // ASCII decode — SNI hostnames are ASCII (Punycode for IDN).
                return System.Text.Encoding.ASCII.GetString(bytes);
            }
            p += extLen;
        }
        return null;
    }

    private static void PrintSummary() {
        Console.WriteLine();
        Console.WriteLine("=== PktmonProbe Summary ===");
        Console.WriteLine($"Total events seen:           {Interlocked.Read(ref _totalEvents)}");
        Console.WriteLine($"Events with payload data:    {Interlocked.Read(ref _eventsWithPayload)}");
        Console.WriteLine($"TCP/443 packets (event 160): {Interlocked.Read(ref _tcp443Packets)}");
        Console.WriteLine($"Events with TLS record:      {Interlocked.Read(ref _eventsWithTlsRecord)}");
        Console.WriteLine($"Events with ClientHello:     {Interlocked.Read(ref _eventsWithClientHello)}");
        Console.WriteLine($"Events with extracted SNI:   {Interlocked.Read(ref _eventsWithSni)}");
        Console.WriteLine();

        if (_tcp443Samples.Count > 0) {
            Console.WriteLine("TCP/443 packet samples (first 96 bytes of TCP payload):");
            lock (_tcp443Lock) {
                foreach (var sample in _tcp443Samples) {
                    Console.WriteLine($"  {sample}");
                }
            }
            Console.WriteLine();
        }

        Console.WriteLine("Top event IDs by frequency:");
        lock (_eventIdCounts) {
            foreach (var kvp in _eventIdCounts.OrderByDescending(kv => kv.Value).Take(10)) {
                Console.WriteLine($"  ID {kvp.Key,5}: {kvp.Value,8}");
            }
        }
        Console.WriteLine();

        if (_sampleSnis.Count > 0) {
            Console.WriteLine("Sample extracted SNI hostnames:");
            lock (_sampleLock) {
                foreach (var sni in _sampleSnis) {
                    Console.WriteLine($"  {sni}");
                }
            }
        } else {
            Console.WriteLine("(No SNI hostnames extracted.)");
        }
        Console.WriteLine();

        // Dump per-event-ID samples for offline inspection. Knowing the
        // task/opcode/payload names + first-256-bytes hex of each event ID
        // tells us whether packet bytes are inline, indirect, or absent.
        Console.WriteLine("=== Per-event-ID samples (first 3 of each, first 256 bytes) ===");
        lock (_samplesLock) {
            foreach (var kvp in _eventSamples.OrderBy(k => k.Key)) {
                Console.WriteLine($"--- Event ID {kvp.Key} ---");
                for (var i = 0; i < kvp.Value.Count; i++) {
                    Console.WriteLine($"  [{i}] {kvp.Value[i]}");
                }
            }
        }
        Console.WriteLine();

        // Decision rendering.
        var clientHellos = Interlocked.Read(ref _eventsWithClientHello);
        var snisExtracted = Interlocked.Read(ref _eventsWithSni);
        if (clientHellos == 0) {
            Console.WriteLine("DECISION: RED — no ClientHellos observed in payload data.");
            Console.WriteLine("Either pktmon ETW does not include packet payloads, or no HTTPS handshakes occurred during the run.");
        } else if (snisExtracted == 0) {
            Console.WriteLine("DECISION: AMBIGUOUS — ClientHellos seen but SNI extraction failed.");
            Console.WriteLine("Production parser may still succeed; the probe heuristic is approximate.");
        } else {
            var rate = snisExtracted * 100.0 / clientHellos;
            Console.WriteLine($"DECISION: GREEN — {snisExtracted}/{clientHellos} ClientHellos yielded SNI ({rate:F0}%).");
            Console.WriteLine("Production-grade parser should perform at least as well.");
        }
    }

    private static int TryRunPktmon(string args) {
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
            Console.Error.WriteLine($"pktmon {args} failed: {ex.Message}");
            return -1;
        }
    }

    private static void TryStopStaleEtwSession(string name) {
        try {
            using var session = TraceEventSession.GetActiveSession(name);
            session?.Stop(noThrow: true);
        } catch {
            // Best effort — if no stale session exists, this throws. Ignore.
        }
    }

    private static bool IsAdmin() {
        try {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        } catch {
            return false;
        }
    }
}
