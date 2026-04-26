using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Beholder.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon.Windows;

/// <summary>
/// Decorator over an inner <see cref="IDnsCache"/> that adds a reverse-DNS
/// (PTR) fallback for IPs the inner cache has never seen. The fast-path
/// <see cref="Resolve"/> stays fully synchronous and never blocks: a miss
/// enqueues the IP for a background worker to look up, then returns
/// <c>null</c> immediately. The first flush tick after seeing a new
/// direct-IP destination shows the raw IP; once the worker resolves it
/// (typically 100-500 ms), it writes the (hostname, IP) pair into the
/// inner cache via <see cref="IDnsCacheIngest.IngestResolved"/> and
/// subsequent flush ticks see the resolved name.
/// </summary>
/// <remarks>
/// Behaviour gates and short-circuits, in order of priority:
/// <list type="number">
///   <item>Inner-cache hit → return the inner value, no PTR query.</item>
///   <item><c>DnsOptions.EnableReverseDnsFallback == false</c> → return
///     <c>null</c> (pure passthrough). Snapshot at construction; no hot
///     reload, matching the existing <c>EnablePreload</c> behaviour.</item>
///   <item><see cref="IPAddressExtensions.IsPrivateOrReserved"/> → return
///     <c>null</c>. RFC 1918 / link-local / loopback / ULA / CGNAT have no
///     meaningful PTR records and would only burn the local resolver.</item>
///   <item>IP currently in <c>_pending</c> → return <c>null</c> (lookup in
///     flight); the next flush tick for this IP will return whatever the
///     worker resolved.</item>
///   <item>IP in <c>_negative</c> within cooldown → return <c>null</c>.
///     Negative entries cap the retry rate for IPs that genuinely have no
///     PTR (cloud machines often don't).</item>
///   <item>Otherwise: enqueue + return <c>null</c>. Queue is bounded with
///     <c>DropWrite</c> so a torrent peer-list burst can't grow the queue
///     unboundedly; if the channel is full this IP is skipped and
///     reconsidered next flush tick (no pending-flag leak — we only mark
///     pending on a successful enqueue).</item>
/// </list>
/// See ADR 005 for the rationale and how this scopes ADR 004's blanket
/// "no outbound DNS" rule down to the direct-IP residual class.
/// </remarks>
public sealed class ReverseDnsFallbackCache
    : IDnsCache, IHostedService, IAsyncDisposable, IDisposable {

    private const int QueueCapacity = 500;
    private static readonly TimeSpan NegativeCacheCooldown = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(5);

    private readonly IDnsCache _inner;
    private readonly IDnsCacheIngest _ingest;
    private readonly IDnsHostnameBackfill _backfill;
    private readonly IReverseDnsResolver _resolver;
    private readonly DnsOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReverseDnsFallbackCache> _logger;

    private readonly ConcurrentDictionary<IPAddress, byte> _pending = new();
    private readonly ConcurrentDictionary<IPAddress, DateTimeOffset> _negative = new();
    private readonly Channel<IPAddress> _queue;

    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private long _resolved;
    private long _failed;
    private bool _disposed;

    /// <summary>
    /// Exposed for tests so they can poll for "the worker has finished
    /// processing N lookups" deterministically instead of sleeping. Sum of
    /// successful + failed; both are written inside the worker's
    /// <c>ProcessOneAsync</c> finally block.
    /// </summary>
    internal int LookupsAttempted =>
        (int)(Interlocked.Read(ref _resolved) + Interlocked.Read(ref _failed));

    public ReverseDnsFallbackCache(
        IDnsCache inner,
        IDnsCacheIngest ingest,
        IDnsHostnameBackfill backfill,
        IReverseDnsResolver resolver,
        IOptionsMonitor<DnsOptions> options,
        TimeProvider timeProvider,
        ILogger<ReverseDnsFallbackCache> logger
    ) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(backfill);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _inner = inner;
        _ingest = ingest;
        _backfill = backfill;
        _resolver = resolver;
        // Snapshot at construction. EnableReverseDnsFallback can't be
        // hot-reloaded — matches EnablePreload's contract on the same options
        // object; a config flip requires daemon restart.
        _options = options.CurrentValue;
        _timeProvider = timeProvider;
        _logger = logger;
        _queue = Channel.CreateBounded<IPAddress>(new BoundedChannelOptions(QueueCapacity) {
            // DropWrite (not DropOldest): silently evicting a queued IP that's
            // already marked _pending would leak the pending flag — the IP
            // would never be re-enqueued. With DropWrite the TryWrite returns
            // false, we don't add to _pending, and the next flush tick simply
            // re-considers the IP.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public string? Resolve(IPAddress address) {
        ArgumentNullException.ThrowIfNull(address);

        var inner = _inner.Resolve(address);
        if (inner is not null) return inner;

        if (!_options.EnableReverseDnsFallback) return null;
        if (address.IsPrivateOrReserved()) return null;
        if (_pending.ContainsKey(address)) return null;

        if (_negative.TryGetValue(address, out var failedAt)) {
            if (_timeProvider.GetUtcNow() - failedAt < NegativeCacheCooldown) return null;
            // Cooldown expired: drop the negative entry so the enqueue below
            // re-attempts the lookup.
            _negative.TryRemove(address, out _);
        }

        // Mark pending only on a successful enqueue. If the channel is full
        // we skip both — the next call for this IP will re-attempt.
        if (_queue.Writer.TryWrite(address)) {
            _pending.TryAdd(address, 0);
        }
        return null;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Detach from the StartAsync caller's context — the worker lives
        // for the daemon's lifetime, not the StartAsync activation scope.
        _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation(
            "Reverse-DNS fallback worker started (enabled={Enabled})",
            _options.EnableReverseDnsFallback);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        if (_cts is null) return;

        _cts.Cancel();
        _queue.Writer.TryComplete();

        if (_workerTask is not null) {
            try {
                // Bound the wait. The loop honours cancellation cooperatively,
                // but a single PTR query already past SystemReverseDnsResolver's
                // own 3 s timeout shouldn't hold daemon shutdown forever.
                await _workerTask.WaitAsync(StopGracePeriod, cancellationToken).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // Expected — either the worker honoured our cancel or the
                // outer cancellationToken fired during host shutdown.
            } catch (TimeoutException) {
                _logger.LogWarning(
                    "Reverse-DNS fallback worker did not stop within {GraceSeconds} s; abandoning",
                    StopGracePeriod.TotalSeconds);
            }
        }

        _logger.LogInformation(
            "Reverse-DNS fallback worker stopped (resolved={Resolved}, failed={Failed})",
            Interlocked.Read(ref _resolved),
            Interlocked.Read(ref _failed));
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken) {
        try {
            await foreach (var address in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) {
                await ProcessOneAsync(address, cancellationToken).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // Shutdown — normal exit.
        } catch (Exception ex) {
            // Outer-boundary catch: a worker crash must not take down the
            // daemon. Any unexpected exception that escapes ProcessOneAsync
            // is a bug; log loudly and exit the loop. The decorator
            // continues to serve inner-cache hits and short-circuit returns.
            _logger.LogError(ex, "Reverse-DNS fallback worker loop crashed");
        }
    }

    private async Task ProcessOneAsync(IPAddress address, CancellationToken cancellationToken) {
        var succeeded = false;
        try {
            var hostname = await _resolver.ResolveAsync(address, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(hostname)) {
                _ingest.IngestResolved(hostname, address);
                // Clear any prior negative entry — the IP now has a name and
                // a future cache eviction shouldn't reapply the cooldown.
                _negative.TryRemove(address, out _);

                // Backfill historical SQLite rows so one-off flows that
                // ended before this lookup completed pick up the resolved
                // name retroactively. Failure here must NOT poison the
                // worker — the in-memory ingest already succeeded and live
                // traffic still resolves; only persisted history misses out.
                var backfilled = 0;
                try {
                    backfilled = await _backfill
                        .BackfillHostnameAsync(address, hostname, cancellationToken)
                        .ConfigureAwait(false);
                } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    // Shutdown in flight — propagate so the worker exits.
                    throw;
                } catch (Exception ex) {
                    _logger.LogWarning(ex,
                        "Reverse-DNS backfill failed for {Address} (hostname already in memory cache)",
                        address);
                }

                _logger.LogDebug(
                    "Reverse-DNS resolved {Address} -> {Hostname} (backfilled {Updated} rows)",
                    address, hostname, backfilled);
                succeeded = true;
            } else {
                _negative[address] = _timeProvider.GetUtcNow();
                _logger.LogDebug("Reverse-DNS failed for {Address}", address);
            }
        } finally {
            // Order of cleanup matters for deterministic test polling: clear
            // the pending flag first so a follow-up Resolve sees consistent
            // state, then bump the counter (LookupsAttempted) so a test that
            // polls the counter is guaranteed all per-lookup side effects
            // have already landed by the time the bump is observed.
            _pending.TryRemove(address, out _);
            if (succeeded) {
                Interlocked.Increment(ref _resolved);
            } else {
                Interlocked.Increment(ref _failed);
            }
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        Dispose();
        GC.SuppressFinalize(this);
    }
}
