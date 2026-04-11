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
/// from the same <see cref="RunAsync"/> task, so the aggregation dictionary is never
/// touched from two threads at once and no lock is required.
/// </summary>
internal sealed class Accumulator {
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly ChannelReader<FlowEvent> _reader;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Accumulator> _logger;
    private readonly Dictionary<string, ProcessAggregate> _aggregates = new(StringComparer.Ordinal);

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
                while (_reader.TryRead(out var flowEvent)) RecordEvent(flowEvent);

                var now = _timeProvider.GetUtcNow();
                if (now >= nextFlush) {
                    FlushTick(now);
                    nextFlush = now + FlushInterval;
                    continue;
                }

                await WaitForEventOrTickAsync(nextFlush - now, cancellationToken).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Expected on shutdown.
        } finally {
            FlushTick(_timeProvider.GetUtcNow());
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

    private async Task WaitForEventOrTickAsync(TimeSpan waitBudget, CancellationToken cancellationToken) {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitTask = _reader.WaitToReadAsync(linkedCts.Token).AsTask();
        var delayTask = Task.Delay(waitBudget, _timeProvider, linkedCts.Token);

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
