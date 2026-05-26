using System.Threading.Channels;
using Beholder.Core;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Hosted service that wires the telemetry hot path end-to-end:
/// <see cref="IFlowSource"/> → bounded <see cref="Channel{FlowEvent}"/> →
/// <see cref="TrafficEngine"/> → <see cref="CounterSnapshot"/> batch logging
/// + SQLite traffic bucket persistence.
///
/// Owns the channel, the engine instance, and the lifecycle of the underlying
/// flow source; no other component sees the channel.
/// </summary>
internal sealed class FlowEventPipeline : IHostedService, IAsyncDisposable, ISnapshotBatchSource, IProcessFirstNetworkFlowSource {
    private const int ChannelCapacity = 10_000;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly IFlowSource _flowSource;
    private readonly IRecordingSettingsState _recordingSettings;
    private readonly ILogger<FlowEventPipeline> _logger;
    private readonly Channel<FlowEvent> _channel;
    private readonly TrafficEngine _engine;

    private CancellationTokenSource? _engineCts;
    private Task? _engineTask;
    private bool _subscribedToFlowSource;
    private bool _subscribedToEngine;
    private bool _subscribedToEngineFirstFlow;
    private bool _disposed;

    /// <summary>
    /// Fires on every engine tick with the snapshot batch. Handlers run
    /// on the engine loop thread and must not block.
    /// </summary>
    public event Action<IReadOnlyList<CounterSnapshot>>? OnSnapshotBatch;

    /// <summary>
    /// Forwards <see cref="TrafficEngine.OnProcessFirstNetworkFlow"/> to
    /// external subscribers. Phase 7's <c>NewProcessDetector</c> consumes
    /// this through the <see cref="IProcessFirstNetworkFlowSource"/>
    /// interface so it never sees the engine directly.
    /// </summary>
    public event Action<string>? OnProcessFirstNetworkFlow;

    /// <summary>
    /// Returns a snapshot of every process the pipeline's engine currently
    /// tracks. Safe to call from any thread. Used by the <c>GetSnapshot</c> RPC.
    /// </summary>
    public Task<IReadOnlyList<CounterSnapshot>> GetCurrentSnapshotsAsync(
        CancellationToken cancellationToken
    ) => _engine.GetCurrentSnapshotsAsync(cancellationToken);

    public FlowEventPipeline(
        IFlowSource flowSource,
        TimeProvider timeProvider,
        ITrafficStore trafficStore,
        IDnsCacheStore dnsCacheStore,
        IDnsCache dnsCache,
        IOptionsMonitor<TrafficStorageOptions> options,
        IRecordingSettingsState recordingSettings,
        ILogger<FlowEventPipeline> logger,
        ILoggerFactory loggerFactory
    ) {
        ArgumentNullException.ThrowIfNull(flowSource);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(trafficStore);
        ArgumentNullException.ThrowIfNull(dnsCacheStore);
        ArgumentNullException.ThrowIfNull(dnsCache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recordingSettings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _flowSource = flowSource;
        _recordingSettings = recordingSettings;
        _logger = logger;

        _channel = Channel.CreateBounded<FlowEvent>(new BoundedChannelOptions(ChannelCapacity) {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _engine = new TrafficEngine(
            _channel.Reader,
            timeProvider,
            trafficStore,
            dnsCacheStore,
            dnsCache,
            options,
            loggerFactory.CreateLogger<TrafficEngine>());
    }

    public async Task StartAsync(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engineTask is not null) throw new InvalidOperationException("Flow event pipeline already started.");

        _flowSource.OnFlowEvent += OnFlowEventReceived;
        _subscribedToFlowSource = true;

        _engine.OnSnapshotBatch += OnSnapshotBatchReceived;
        _subscribedToEngine = true;

        _engine.OnProcessFirstNetworkFlow += OnProcessFirstNetworkFlowReceived;
        _subscribedToEngineFirstFlow = true;

        try {
            await _flowSource.StartAsync(cancellationToken).ConfigureAwait(false);
        } catch {
            _flowSource.OnFlowEvent -= OnFlowEventReceived;
            _subscribedToFlowSource = false;
            _engine.OnSnapshotBatch -= OnSnapshotBatchReceived;
            _subscribedToEngine = false;
            _engine.OnProcessFirstNetworkFlow -= OnProcessFirstNetworkFlowReceived;
            _subscribedToEngineFirstFlow = false;
            throw;
        }

        _engineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _engineTask = _engine.RunAsync(_engineCts.Token);

        _logger.LogInformation("Flow event pipeline started");
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        _logger.LogInformation("Flow event pipeline stopping");

        if (_subscribedToFlowSource) {
            _flowSource.OnFlowEvent -= OnFlowEventReceived;
            _subscribedToFlowSource = false;
        }

        _channel.Writer.TryComplete();

        _engineCts?.Cancel();

        if (_engineTask is not null) {
            var completed = await Task.WhenAny(
                _engineTask,
                Task.Delay(StopTimeout, cancellationToken)).ConfigureAwait(false);
            if (completed != _engineTask) {
                _logger.LogWarning(
                    "TrafficEngine loop did not complete within {StopTimeout} of shutdown",
                    StopTimeout);
            }
            _engineTask = null;
        }

        if (_subscribedToEngine) {
            _engine.OnSnapshotBatch -= OnSnapshotBatchReceived;
            _subscribedToEngine = false;
        }

        if (_subscribedToEngineFirstFlow) {
            _engine.OnProcessFirstNetworkFlow -= OnProcessFirstNetworkFlowReceived;
            _subscribedToEngineFirstFlow = false;
        }

        await _flowSource.StopAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Flow event pipeline stopped");
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) return;
        _disposed = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _engineCts?.Dispose();
        _engineCts = null;
        GC.SuppressFinalize(this);
    }

    private void OnFlowEventReceived(FlowEvent flowEvent) {
        // Drop Beholder's own traffic before it enters any pipeline stage.
        // The state singleton is read per-event so flipping the toggle in
        // Settings (or in appsettings.json + restart) takes effect on the
        // next event. Phase 13.2 routes this through IRecordingSettingsState
        // instead of IOptionsMonitor<RecordingOptions> so the toggle can be
        // mutated at runtime via the SetRecordingSettings RPC.
        if (_recordingSettings.FilterSelfTraffic
            && SelfTrafficFilter.IsSelfProcess(flowEvent.ProcessPath))
            return;

        if (!_channel.Writer.TryWrite(flowEvent)) {
            _logger.LogWarning(
                "Flow event channel write failed — pipeline is in an unexpected state");
        }
    }

    private void OnSnapshotBatchReceived(IReadOnlyList<CounterSnapshot> snapshots) {
        foreach (var snapshot in snapshots) {
            _logger.LogDebug(
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

    private void OnProcessFirstNetworkFlowReceived(string processPath) =>
        OnProcessFirstNetworkFlow?.Invoke(processPath);
}
