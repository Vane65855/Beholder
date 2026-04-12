using System.Net;
using System.Threading.Channels;
using Beholder.Core;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Consumes <see cref="FlowEvent"/> records from a channel and emits per-process
/// <see cref="CounterSnapshot"/> batches on a fixed tick interval. The Accumulator owns
/// the running byte totals for every process it has observed since start-up; on each
/// tick it builds a snapshot for every process that had activity during the tick,
/// resets the per-tick delta state, and fires <see cref="OnSnapshotBatch"/>.
///
/// The loop is single-consumer by design: both channel draining and tick flushing run
/// from the same <see cref="RunAsync"/> task. The aggregation dictionary is also
/// readable from other threads via <see cref="GetCurrentSnapshotsAsync"/>, which is
/// why all mutation and reads of <c>_aggregates</c> are serialized through a
/// <see cref="SemaphoreSlim"/>.
/// </summary>
internal sealed class Accumulator {
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly ChannelReader<FlowEvent> _reader;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Accumulator> _logger;
    private readonly Dictionary<string, ProcessAggregate> _aggregates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _aggregatesLock = new(1, 1);
    private TaskCompletionSource? _waitingForTick;

    public Accumulator(
        ChannelReader<FlowEvent> reader,
        TimeProvider timeProvider,
        ILogger<Accumulator> logger
    ) {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Fires once per tick with the batch of snapshots for processes that had activity
    /// during the tick. Not fired when the tick would produce an empty batch.
    /// </summary>
    public event Action<IReadOnlyList<CounterSnapshot>>? OnSnapshotBatch;

    /// <summary>Diagnostic. Test-only — installs a signal that fires once the loop has
    /// registered its delay timer with the TimeProvider, guaranteeing a subsequent
    /// <c>FakeTimeProvider.Advance</c> will fire it.</summary>
    internal void SetWaitSignal(TaskCompletionSource signal)
        => Interlocked.Exchange(ref _waitingForTick, signal);

    /// <summary>
    /// Runs the accumulator loop until <paramref name="cancellationToken"/> is signaled.
    /// Reads events from the channel as fast as they arrive and flushes accumulated
    /// state on every <see cref="FlushInterval"/> elapse of <see cref="TimeProvider"/>.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken) {
        var nextFlush = _timeProvider.GetUtcNow() + FlushInterval;
        _logger.LogInformation("Accumulator loop starting with {FlushInterval} flush interval", FlushInterval);
        try {
            while (!cancellationToken.IsCancellationRequested) {
                await _aggregatesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try {
                    while (_reader.TryRead(out var flowEvent)) RecordEvent(flowEvent);
                } finally {
                    _aggregatesLock.Release();
                }

                var now = _timeProvider.GetUtcNow();
                if (now >= nextFlush) {
                    await _aggregatesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try {
                        FlushTick(now);
                    } finally {
                        _aggregatesLock.Release();
                    }
                    nextFlush = now + FlushInterval;
                    continue;
                }

                await WaitForEventOrTickAsync(nextFlush - now, cancellationToken).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Expected on shutdown.
        } finally {
            await _aggregatesLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try {
                FlushTick(_timeProvider.GetUtcNow());
            } finally {
                _aggregatesLock.Release();
            }
        }
        _logger.LogInformation("Accumulator loop stopped");
    }

    private void RecordEvent(FlowEvent flowEvent) {
        if (!_aggregates.TryGetValue(flowEvent.ProcessPath, out var aggregate)) {
            aggregate = new ProcessAggregate(flowEvent.ProcessName, flowEvent.ProcessPath);
            _aggregates[flowEvent.ProcessPath] = aggregate;
        }
        aggregate.Record(flowEvent);
    }

    private void FlushTick(DateTimeOffset timestamp) {
        List<CounterSnapshot>? batch = null;
        foreach (var aggregate in _aggregates.Values) {
            if (!aggregate.HasActivityThisTick) continue;
            batch ??= new List<CounterSnapshot>();
            batch.Add(aggregate.BuildSnapshot(timestamp));
            aggregate.ResetTick();
        }
        if (batch is null) return;
        OnSnapshotBatch?.Invoke(batch);
    }

    /// <summary>
    /// Snapshots every process the accumulator currently tracks, including those
    /// without activity in the most recent tick. Unlike the tick flush, this does
    /// not reset per-tick delta state — it is a read-only view intended for the
    /// <c>GetSnapshot</c> RPC, not a replacement for the tick fan-out.
    /// </summary>
    public async Task<IReadOnlyList<CounterSnapshot>> GetCurrentSnapshotsAsync(
        CancellationToken cancellationToken
    ) {
        await _aggregatesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (_aggregates.Count == 0) return Array.Empty<CounterSnapshot>();
            var now = _timeProvider.GetUtcNow();
            var snapshots = new List<CounterSnapshot>(_aggregates.Count);
            foreach (var aggregate in _aggregates.Values) {
                snapshots.Add(aggregate.BuildSnapshot(now));
            }
            return snapshots;
        } finally {
            _aggregatesLock.Release();
        }
    }

    private async Task WaitForEventOrTickAsync(TimeSpan waitBudget, CancellationToken cancellationToken) {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitTask = _reader.WaitToReadAsync(linkedCts.Token).AsTask();
        var delayTask = Task.Delay(waitBudget, _timeProvider, linkedCts.Token);

        // Signal that the delay timer is now registered with the TimeProvider.
        Interlocked.Exchange(ref _waitingForTick, null)?.TrySetResult();

        var completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
        linkedCts.Cancel();

        var loser = ReferenceEquals(completed, waitTask) ? delayTask : waitTask;
        try {
            await loser.ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Expected: we cancelled the loser ourselves to release its resources.
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class ProcessAggregate {
        public ProcessAggregate(string processName, string processPath) {
            ProcessName = processName;
            ProcessPath = processPath;
        }

        public string ProcessName { get; }
        public string ProcessPath { get; }
        public long TotalBytesIn { get; private set; }
        public long TotalBytesOut { get; private set; }
        public long DeltaBytesIn { get; private set; }
        public long DeltaBytesOut { get; private set; }

        private readonly HashSet<(IPAddress Address, int Port)> _activeConnections = new();
        private readonly Dictionary<CountryCode, long> _bytesOutByCountry = new();

        public bool HasActivityThisTick =>
            DeltaBytesIn > 0 || DeltaBytesOut > 0 || _activeConnections.Count > 0;

        public void Record(FlowEvent flowEvent) {
            TotalBytesIn += flowEvent.BytesIn;
            TotalBytesOut += flowEvent.BytesOut;
            DeltaBytesIn += flowEvent.BytesIn;
            DeltaBytesOut += flowEvent.BytesOut;
            _activeConnections.Add((flowEvent.RemoteAddress, flowEvent.RemotePort));
            if (flowEvent.BytesOut > 0) {
                _bytesOutByCountry.TryGetValue(flowEvent.Country, out var existing);
                _bytesOutByCountry[flowEvent.Country] = existing + flowEvent.BytesOut;
            }
        }

        public CounterSnapshot BuildSnapshot(DateTimeOffset timestamp) {
            return new CounterSnapshot(
                processName: ProcessName,
                processPath: ProcessPath,
                totalBytesIn: TotalBytesIn,
                totalBytesOut: TotalBytesOut,
                deltaBytesIn: DeltaBytesIn,
                deltaBytesOut: DeltaBytesOut,
                activeConnectionCount: _activeConnections.Count,
                bytesOutByCountry: _bytesOutByCountry,
                timestamp: timestamp);
        }

        public void ResetTick() {
            DeltaBytesIn = 0;
            DeltaBytesOut = 0;
            _activeConnections.Clear();
            _bytesOutByCountry.Clear();
        }
    }
}
