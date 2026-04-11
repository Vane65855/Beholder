using System.Threading.Channels;
using Beholder.Core;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Hosted service that wires the telemetry hot path end-to-end:
/// <see cref="IFlowSource"/> → bounded <see cref="Channel{FlowEvent}"/> →
/// <see cref="Accumulator"/> → <see cref="CounterSnapshot"/> batch logging.
/// Owns the channel, the accumulator instance, and the lifecycle of the
/// underlying flow source; no other component sees the channel.
///
/// The flow source raises events on its own thread (ETW callback thread on
/// Windows). The pipeline's handler writes each event into the bounded channel
/// and returns immediately; the accumulator's dedicated task drains the channel
/// and emits per-second snapshot batches, which this service logs at
/// Information level.
/// </summary>
internal sealed class FlowEventPipeline : IHostedService, IAsyncDisposable, ISnapshotBatchSource {
    private const int ChannelCapacity = 10_000;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly IFlowSource _flowSource;
    private readonly ILogger<FlowEventPipeline> _logger;
    private readonly Channel<FlowEvent> _channel;
    private readonly Accumulator _accumulator;

    private CancellationTokenSource? _accumulatorCts;
    private Task? _accumulatorTask;
    private bool _subscribedToFlowSource;
    private bool _subscribedToAccumulator;
    private bool _disposed;

    /// <summary>
    /// Fires on every accumulator tick with the snapshot batch. Handlers run
    /// on the accumulator loop thread and must not block.
    /// </summary>
    public event Action<IReadOnlyList<CounterSnapshot>>? OnSnapshotBatch;

    public FlowEventPipeline(
        IFlowSource flowSource,
        TimeProvider timeProvider,
        ILogger<FlowEventPipeline> logger,
        ILoggerFactory loggerFactory
    ) {
        ArgumentNullException.ThrowIfNull(flowSource);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _flowSource = flowSource;
        _logger = logger;

        // Bounded at 10,000 events: roughly 10 s of headroom at 1,000 events/sec,
        // which comfortably covers the accumulator's 1 Hz flush cadence and normal
        // traffic bursts without wasting memory.
        //
        // DropOldest: if the accumulator falls behind, we lose old events rather
        // than blocking the ETW callback. Blocking inside an ETW callback can stall
        // the entire trace session — catastrophic. Dropping oldest also keeps the
        // daemon's view aligned with "now" rather than lagging behind real traffic.
        //
        // SingleReader: the Accumulator is the sole reader — enables the channel's
        // faster internal path.
        //
        // SingleWriter = false: the ETW processing thread can deliver events from
        // multiple threads depending on provider buffering; the channel must
        // tolerate concurrent writers.
        _channel = Channel.CreateBounded<FlowEvent>(new BoundedChannelOptions(ChannelCapacity) {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _accumulator = new Accumulator(
            _channel.Reader,
            timeProvider,
            loggerFactory.CreateLogger<Accumulator>());
    }

    public async Task StartAsync(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_accumulatorTask is not null) throw new InvalidOperationException("Flow event pipeline already started.");

        _flowSource.OnFlowEvent += OnFlowEventReceived;
        _subscribedToFlowSource = true;

        _accumulator.OnSnapshotBatch += OnSnapshotBatchReceived;
        _subscribedToAccumulator = true;

        try {
            await _flowSource.StartAsync(cancellationToken).ConfigureAwait(false);
        } catch {
            _flowSource.OnFlowEvent -= OnFlowEventReceived;
            _subscribedToFlowSource = false;
            _accumulator.OnSnapshotBatch -= OnSnapshotBatchReceived;
            _subscribedToAccumulator = false;
            throw;
        }

        _accumulatorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _accumulatorTask = _accumulator.RunAsync(_accumulatorCts.Token);

        _logger.LogInformation("Flow event pipeline started");
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        _logger.LogInformation("Flow event pipeline stopping");

        if (_subscribedToFlowSource) {
            _flowSource.OnFlowEvent -= OnFlowEventReceived;
            _subscribedToFlowSource = false;
        }

        // Idempotent signal to the accumulator: no more events are coming.
        _channel.Writer.TryComplete();

        _accumulatorCts?.Cancel();

        if (_accumulatorTask is not null) {
            var completed = await Task.WhenAny(
                _accumulatorTask,
                Task.Delay(StopTimeout, cancellationToken)).ConfigureAwait(false);
            if (completed != _accumulatorTask) {
                _logger.LogWarning(
                    "Accumulator loop did not complete within {StopTimeout} of shutdown",
                    StopTimeout);
            }
            _accumulatorTask = null;
        }

        // Unsubscribe the batch handler AFTER the accumulator stops so any final
        // in-flight flush still reaches the logger.
        if (_subscribedToAccumulator) {
            _accumulator.OnSnapshotBatch -= OnSnapshotBatchReceived;
            _subscribedToAccumulator = false;
        }

        // Stop the flow source LAST so any events in flight during the accumulator
        // drain window still had a live provider behind them.
        await _flowSource.StopAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Flow event pipeline stopped");
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) return;
        _disposed = true;
        // StopAsync is idempotent and safe when never started.
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _accumulatorCts?.Dispose();
        _accumulatorCts = null;
        GC.SuppressFinalize(this);
    }

    private void OnFlowEventReceived(FlowEvent flowEvent) {
        // DropOldest means TryWrite essentially always succeeds at steady state —
        // it evicts the oldest queued item to make room. A false return only
        // happens during shutdown, when a straggling ETW callback arrives after
        // the writer has been completed. Log once and move on; we cannot afford
        // to throw back into the ETW callback thread.
        if (!_channel.Writer.TryWrite(flowEvent)) {
            _logger.LogWarning(
                "Flow event channel write failed — pipeline is in an unexpected state");
        }
    }

    private void OnSnapshotBatchReceived(IReadOnlyList<CounterSnapshot> snapshots) {
        foreach (var snapshot in snapshots) {
            _logger.LogInformation(
                "Counter {Process} Δin={DeltaIn} Δout={DeltaOut} total_in={TotalIn} total_out={TotalOut} conns={Connections}",
                snapshot.ProcessName,
                snapshot.DeltaBytesIn,
                snapshot.DeltaBytesOut,
                snapshot.TotalBytesIn,
                snapshot.TotalBytesOut,
                snapshot.ActiveConnectionCount);
        }
        OnSnapshotBatch?.Invoke(snapshots);
    }
}
