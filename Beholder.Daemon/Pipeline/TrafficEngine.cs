using System.Net;
using System.Threading.Channels;
using Beholder.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Consumes <see cref="FlowEvent"/> records from a channel and produces TWO
/// outputs from the same enriched event stream:
///
/// 1. Per-second <see cref="CounterSnapshot"/> batches for the live IPC stream
///    (in-memory, ephemeral).
/// 2. Per-second <see cref="TrafficBucket"/> rows persisted to <c>traffic_raw</c>
///    via <see cref="ITrafficStore.WriteRawBucketsAsync"/>. The rollup service
///    cascades these into coarser tiers.
///
/// The engine holds only the active working set in memory: destinations
/// currently accumulating into the open raw bucket, plus per-process
/// session-scoped lifetime totals. Everything else is a SQL query against the
/// tiered storage.
/// </summary>
internal sealed class TrafficEngine {
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly ChannelReader<FlowEvent> _reader;
    private readonly TimeProvider _timeProvider;
    private readonly ITrafficStore _trafficStore;
    private readonly IDnsCacheStore _dnsCacheStore;
    private readonly IDnsCache _dnsCache;
    private readonly IOptionsMonitor<TrafficStorageOptions> _options;
    private readonly ILogger<TrafficEngine> _logger;

    private readonly Dictionary<DestinationKey, DestinationAggregate> _destinations = new();
    private readonly Dictionary<string, ProcessLifetimeTotals> _processLifetimeTotals
        = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private TaskCompletionSource? _waitingForTick;

    private DateTimeOffset _currentRawBucketStart;

    public TrafficEngine(
        ChannelReader<FlowEvent> reader,
        TimeProvider timeProvider,
        ITrafficStore trafficStore,
        IDnsCacheStore dnsCacheStore,
        IDnsCache dnsCache,
        IOptionsMonitor<TrafficStorageOptions> options,
        ILogger<TrafficEngine> logger
    ) {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(trafficStore);
        ArgumentNullException.ThrowIfNull(dnsCacheStore);
        ArgumentNullException.ThrowIfNull(dnsCache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _timeProvider = timeProvider;
        _trafficStore = trafficStore;
        _dnsCacheStore = dnsCacheStore;
        _dnsCache = dnsCache;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Fires once per tick with the batch of snapshots for processes that had activity
    /// during the tick. Not fired when the tick would produce an empty batch.
    /// </summary>
    public event Action<IReadOnlyList<CounterSnapshot>>? OnSnapshotBatch;

    /// <summary>
    /// Fires the first time a given process path appears in the engine's
    /// in-memory lifetime totals. Phase 7's <c>NewProcessDetector</c>
    /// subscribes here rather than to the raw <c>IFlowSource</c> stream
    /// because (a) the engine consumer thread is safe for detector work
    /// while the ETW callback thread is not, and (b) firing on every event
    /// would spam the detector — this is fire-once-per-key for the
    /// engine's session-scoped view. Daemon-restart deduplication is the
    /// detector's job: it consults <c>IProcessRegistry</c> to suppress
    /// re-alerts for binaries already registered across restarts.
    /// </summary>
    public event Action<string>? OnProcessFirstNetworkFlow;

    /// <summary>Diagnostic. Test-only — installs a signal that fires once the loop has
    /// registered its delay timer with the TimeProvider.</summary>
    internal void SetWaitSignal(TaskCompletionSource signal)
        => Interlocked.Exchange(ref _waitingForTick, signal);

    /// <summary>
    /// Runs the engine loop until <paramref name="cancellationToken"/> is signaled.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken) {
        var now = _timeProvider.GetUtcNow();
        _currentRawBucketStart = AlignToSecondBoundary(now);
        var nextFlush = now + TickInterval;

        // RawFlushInterval intentionally mirrors TickInterval — each tick
        // closes one raw-tier bucket. Logged as a distinct field so the line
        // reads correctly if the two are ever decoupled.
        _logger.LogInformation(
            "TrafficEngine loop starting with {TickInterval} tick, {RawFlushInterval} raw flush",
            TickInterval, TickInterval);

        try {
            while (!cancellationToken.IsCancellationRequested) {
                await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try {
                    while (_reader.TryRead(out var flowEvent)) RecordEvent(flowEvent);
                } finally {
                    _lock.Release();
                }

                now = _timeProvider.GetUtcNow();
                if (now >= nextFlush) {
                    await FlushTickAndRawAsync(now, cancellationToken).ConfigureAwait(false);
                    nextFlush = now + TickInterval;
                    continue;
                }

                await WaitForEventOrTickAsync(nextFlush - now, cancellationToken).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Expected on shutdown.
        } finally {
            // Final flush: persist any remaining raw data before stopping.
            await FlushTickAndRawAsync(_timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }

        _logger.LogInformation("TrafficEngine loop stopped");
    }

    /// <summary>
    /// Snapshots every process the engine currently tracks. Read-only view for the
    /// <c>GetSnapshot</c> RPC — does not reset per-tick delta state.
    /// </summary>
    public async Task<IReadOnlyList<CounterSnapshot>> GetCurrentSnapshotsAsync(
        CancellationToken cancellationToken
    ) {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (_processLifetimeTotals.Count == 0) return Array.Empty<CounterSnapshot>();
            var now = _timeProvider.GetUtcNow();
            return BuildSnapshotsForAllProcesses(now);
        } finally {
            _lock.Release();
        }
    }

    private void RecordEvent(FlowEvent flowEvent) {
        var key = new DestinationKey(
            flowEvent.ProcessPath,
            flowEvent.RemoteAddress.ToString(),
            flowEvent.RemotePort);

        if (!_destinations.TryGetValue(key, out var dest)) {
            dest = new DestinationAggregate(
                flowEvent.ProcessPath,
                flowEvent.ProcessName,
                flowEvent.RemoteAddress.ToString(),
                flowEvent.RemotePort,
                flowEvent.Country);
            _destinations[key] = dest;
        }

        dest.TickBytesIn += flowEvent.BytesIn;
        dest.TickBytesOut += flowEvent.BytesOut;
        dest.RawBytesIn += flowEvent.BytesIn;
        dest.RawBytesOut += flowEvent.BytesOut;
        dest.LastActivity = _timeProvider.GetUtcNow();
        dest.Country = flowEvent.Country;

        // Track active connections per tick for this destination
        dest.ActiveEndpoints.Add((flowEvent.RemoteAddress, flowEvent.RemotePort));

        // Track per-country outbound bytes for this tick
        if (flowEvent.BytesOut > 0) {
            dest.TickBytesOutByCountry.TryGetValue(flowEvent.Country, out var existing);
            dest.TickBytesOutByCountry[flowEvent.Country] = existing + flowEvent.BytesOut;
        }

        // Update session-scoped process lifetime totals
        if (!_processLifetimeTotals.TryGetValue(flowEvent.ProcessPath, out var totals)) {
            totals = new ProcessLifetimeTotals { ProcessName = flowEvent.ProcessName };
            _processLifetimeTotals[flowEvent.ProcessPath] = totals;
            // Fire AFTER the dictionary insert so a subscriber that synchronously
            // re-enters the engine sees the path as already known. The handler
            // runs on the engine consumer thread; subscribers must not block.
            OnProcessFirstNetworkFlow?.Invoke(flowEvent.ProcessPath);
        }
        totals.TotalBytesIn += flowEvent.BytesIn;
        totals.TotalBytesOut += flowEvent.BytesOut;
        totals.LastActivity = _timeProvider.GetUtcNow();
    }

    private async Task FlushTickAndRawAsync(DateTimeOffset timestamp, CancellationToken cancellationToken) {
        List<TrafficBucket>? rawBuckets = null;
        List<(string Address, string Hostname)>? dnsEntries = null;
        IReadOnlyList<CounterSnapshot>? snapshots;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            snapshots = BuildSnapshotsForActiveProcesses(timestamp);

            // Build one raw row per destination that accumulated bytes during
            // this tick, then zero out the per-tick counters.
            foreach (var dest in _destinations.Values) {
                if (dest.RawBytesIn == 0 && dest.RawBytesOut == 0) continue;

                rawBuckets ??= new List<TrafficBucket>();
                var hostname = _dnsCache.Resolve(IPAddress.Parse(dest.RemoteAddress));

                rawBuckets.Add(new TrafficBucket(
                    id: 0,
                    processPath: dest.ProcessPath,
                    processName: dest.ProcessName,
                    remoteAddress: dest.RemoteAddress,
                    remotePort: dest.RemotePort,
                    hostname: hostname,
                    country: dest.Country,
                    bytesIn: dest.RawBytesIn,
                    bytesOut: dest.RawBytesOut,
                    bucketStart: _currentRawBucketStart,
                    bucketSeconds: 1));

                if (hostname is not null) {
                    dnsEntries ??= new List<(string, string)>();
                    dnsEntries.Add((dest.RemoteAddress, hostname));
                }

                dest.RawBytesIn = 0;
                dest.RawBytesOut = 0;
            }

            ResetTickDeltas();
            _currentRawBucketStart = AlignToSecondBoundary(timestamp);

            EvictIdleDestinations(timestamp);
            EvictIdleProcessTotals(timestamp);
        } finally {
            _lock.Release();
        }

        if (snapshots is not null) OnSnapshotBatch?.Invoke(snapshots);

        if (rawBuckets is not null) {
            await _trafficStore.WriteRawBucketsAsync(rawBuckets, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Flushed {BucketCount} raw traffic buckets to SQLite", rawBuckets.Count);
        }

        if (dnsEntries is not null) {
            await _dnsCacheStore.UpsertBatchAsync(dnsEntries, cancellationToken).ConfigureAwait(false);
        }
    }

    private List<CounterSnapshot>? BuildSnapshotsForActiveProcesses(DateTimeOffset timestamp) {
        // Group destinations by process, summing tick deltas
        var processDeltas = new Dictionary<string, (
            long DeltaIn, long DeltaOut,
            HashSet<(IPAddress, int)> Connections,
            Dictionary<CountryCode, long> BytesOutByCountry)>(StringComparer.Ordinal);

        foreach (var dest in _destinations.Values) {
            if (dest.TickBytesIn == 0 && dest.TickBytesOut == 0 && dest.ActiveEndpoints.Count == 0)
                continue;

            if (!processDeltas.TryGetValue(dest.ProcessPath, out var pd)) {
                pd = (0, 0, new HashSet<(IPAddress, int)>(), new Dictionary<CountryCode, long>());
            }

            pd.DeltaIn += dest.TickBytesIn;
            pd.DeltaOut += dest.TickBytesOut;
            foreach (var ep in dest.ActiveEndpoints) pd.Connections.Add(ep);
            foreach (var (country, bytes) in dest.TickBytesOutByCountry) {
                pd.BytesOutByCountry.TryGetValue(country, out var existing);
                pd.BytesOutByCountry[country] = existing + bytes;
            }

            processDeltas[dest.ProcessPath] = pd;
        }

        if (processDeltas.Count == 0) return null;

        var batch = new List<CounterSnapshot>(processDeltas.Count);
        foreach (var (processPath, pd) in processDeltas) {
            var totals = _processLifetimeTotals[processPath];
            batch.Add(new CounterSnapshot(
                processName: totals.ProcessName,
                processPath: processPath,
                totalBytesIn: totals.TotalBytesIn,
                totalBytesOut: totals.TotalBytesOut,
                deltaBytesIn: pd.DeltaIn,
                deltaBytesOut: pd.DeltaOut,
                activeConnectionCount: pd.Connections.Count,
                bytesOutByCountry: pd.BytesOutByCountry,
                timestamp: timestamp));
        }
        return batch;
    }

    private IReadOnlyList<CounterSnapshot> BuildSnapshotsForAllProcesses(DateTimeOffset timestamp) {
        // Group ALL destinations by process for current tick deltas
        var processDeltas = new Dictionary<string, (
            long DeltaIn, long DeltaOut,
            HashSet<(IPAddress, int)> Connections,
            Dictionary<CountryCode, long> BytesOutByCountry)>(StringComparer.Ordinal);

        foreach (var dest in _destinations.Values) {
            if (!processDeltas.TryGetValue(dest.ProcessPath, out var pd)) {
                pd = (0, 0, new HashSet<(IPAddress, int)>(), new Dictionary<CountryCode, long>());
            }
            pd.DeltaIn += dest.TickBytesIn;
            pd.DeltaOut += dest.TickBytesOut;
            foreach (var ep in dest.ActiveEndpoints) pd.Connections.Add(ep);
            foreach (var (country, bytes) in dest.TickBytesOutByCountry) {
                pd.BytesOutByCountry.TryGetValue(country, out var existing);
                pd.BytesOutByCountry[country] = existing + bytes;
            }
            processDeltas[dest.ProcessPath] = pd;
        }

        var snapshots = new List<CounterSnapshot>(_processLifetimeTotals.Count);
        foreach (var (processPath, totals) in _processLifetimeTotals) {
            processDeltas.TryGetValue(processPath, out var pd);
            snapshots.Add(new CounterSnapshot(
                processName: totals.ProcessName,
                processPath: processPath,
                totalBytesIn: totals.TotalBytesIn,
                totalBytesOut: totals.TotalBytesOut,
                deltaBytesIn: pd.DeltaIn,
                deltaBytesOut: pd.DeltaOut,
                activeConnectionCount: pd.Connections?.Count ?? 0,
                bytesOutByCountry: pd.BytesOutByCountry ?? new Dictionary<CountryCode, long>(),
                timestamp: timestamp));
        }
        return snapshots;
    }

    private void ResetTickDeltas() {
        foreach (var dest in _destinations.Values) {
            dest.TickBytesIn = 0;
            dest.TickBytesOut = 0;
            dest.ActiveEndpoints.Clear();
            dest.TickBytesOutByCountry.Clear();
        }
    }

    private void EvictIdleDestinations(DateTimeOffset now) {
        var idleThreshold = now - TimeSpan.FromMinutes(_options.CurrentValue.IdleDestinationTimeoutMinutes);
        var toRemove = new List<DestinationKey>();

        foreach (var (key, dest) in _destinations) {
            if (dest.LastActivity >= idleThreshold) continue;

            // Flush any remaining raw bytes before eviction — never lose data
            if (dest.RawBytesIn > 0 || dest.RawBytesOut > 0) continue;

            toRemove.Add(key);
        }

        foreach (var key in toRemove) _destinations.Remove(key);

        if (toRemove.Count > 0) {
            _logger.LogDebug("Evicted {Count} idle destinations", toRemove.Count);
        }
    }

    private void EvictIdleProcessTotals(DateTimeOffset now) {
        var idleThreshold = now - TimeSpan.FromHours(_options.CurrentValue.IdleProcessTimeoutHours);
        var toRemove = new List<string>();

        foreach (var (path, totals) in _processLifetimeTotals) {
            if (totals.LastActivity >= idleThreshold) continue;
            toRemove.Add(path);
        }

        foreach (var path in toRemove) _processLifetimeTotals.Remove(path);

        if (toRemove.Count > 0) {
            _logger.LogDebug("Evicted {Count} idle process totals", toRemove.Count);
        }
    }

    private async Task WaitForEventOrTickAsync(TimeSpan waitBudget, CancellationToken cancellationToken) {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitTask = _reader.WaitToReadAsync(linkedCts.Token).AsTask();
        var delayTask = Task.Delay(waitBudget, _timeProvider, linkedCts.Token);

        Interlocked.Exchange(ref _waitingForTick, null)?.TrySetResult();

        var completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
        linkedCts.Cancel();

        var loser = ReferenceEquals(completed, waitTask) ? delayTask : waitTask;
        try {
            await loser.ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Expected: we cancelled the loser to release its resources.
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static DateTimeOffset AlignToSecondBoundary(DateTimeOffset time) {
        var epochMs = time.ToUnixTimeMilliseconds();
        var aligned = (epochMs / 1000L) * 1000L;
        return DateTimeOffset.FromUnixTimeMilliseconds(aligned);
    }

    private readonly record struct DestinationKey(
        string ProcessPath, string RemoteAddress, int RemotePort);

    private sealed class DestinationAggregate(
        string processPath,
        string processName,
        string remoteAddress,
        int remotePort,
        CountryCode country
    ) {
        public string ProcessPath { get; } = processPath;
        public string ProcessName { get; } = processName;
        public string RemoteAddress { get; } = remoteAddress;
        public int RemotePort { get; } = remotePort;
        public CountryCode Country { get; set; } = country;

        public long TickBytesIn { get; set; }
        public long TickBytesOut { get; set; }
        public long RawBytesIn { get; set; }
        public long RawBytesOut { get; set; }
        public DateTimeOffset LastActivity { get; set; }

        public HashSet<(IPAddress Address, int Port)> ActiveEndpoints { get; } = new();
        public Dictionary<CountryCode, long> TickBytesOutByCountry { get; } = new();
    }

    private sealed class ProcessLifetimeTotals {
        public required string ProcessName { get; init; }
        public long TotalBytesIn;
        public long TotalBytesOut;
        public DateTimeOffset LastActivity;
    }
}
