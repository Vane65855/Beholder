using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Beholder.Core;
using Beholder.Protocol;
using Local = Beholder.Protocol.Local;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Fans the single <see cref="ISnapshotBatchSource.OnSnapshotBatch"/> event
/// out into a per-subscriber bounded channel. Each subscription gets its own
/// channel so slow consumers drop their own old events instead of blocking the
/// publisher or starving other subscribers.
/// </summary>
internal sealed class BroadcastService : IHostedService, IDisposable {
    private const int DefaultSubscriberChannelCapacity = 100;

    private readonly ISnapshotBatchSource _source;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BroadcastService> _logger;
    private readonly int _subscriberChannelCapacity;
    private readonly ConcurrentDictionary<Guid, Channel<Local.DaemonEvent>> _subscribers = new();
    private bool _subscribed;
    private bool _disposed;

    public BroadcastService(
        ISnapshotBatchSource source,
        TimeProvider timeProvider,
        ILogger<BroadcastService> logger
    ) : this(source, timeProvider, logger, DefaultSubscriberChannelCapacity) { }

    // Test-only overload; internal visibility matches the class.
    internal BroadcastService(
        ISnapshotBatchSource source,
        TimeProvider timeProvider,
        ILogger<BroadcastService> logger,
        int subscriberChannelCapacity
    ) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriberChannelCapacity);

        _source = source;
        _timeProvider = timeProvider;
        _logger = logger;
        _subscriberChannelCapacity = subscriberChannelCapacity;
    }

    /// <summary>Diagnostic. Test-only — lets tests spin-wait for registration.</summary>
    internal int ActiveSubscriberCount => _subscribers.Count;

    public Task StartAsync(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _source.OnSnapshotBatch += OnSnapshotBatch;
        _subscribed = true;
        _logger.LogInformation("Broadcast service started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        if (_subscribed) {
            _source.OnSnapshotBatch -= OnSnapshotBatch;
            _subscribed = false;
        }
        foreach (var (_, channel) in _subscribers) {
            channel.Writer.TryComplete();
        }
        _subscribers.Clear();
        _logger.LogInformation("Broadcast service stopped");
        return Task.CompletedTask;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        if (_subscribed) {
            _source.OnSnapshotBatch -= OnSnapshotBatch;
            _subscribed = false;
        }
        foreach (var (_, channel) in _subscribers) {
            channel.Writer.TryComplete();
        }
        _subscribers.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Returns an async stream of <see cref="Local.DaemonEvent"/> for one
    /// subscriber. The subscriber's channel is created and registered on first
    /// <c>MoveNextAsync</c> and removed when the caller stops enumerating
    /// (cancellation or normal completion).
    /// </summary>
    public async IAsyncEnumerable<Local.DaemonEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken
    ) {
        var subscriberId = Guid.NewGuid();
        var channel = Channel.CreateBounded<Local.DaemonEvent>(
            new BoundedChannelOptions(_subscriberChannelCapacity) {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        _subscribers.TryAdd(subscriberId, channel);
        _logger.LogInformation("Broadcast subscriber {Id} added", subscriberId);
        try {
            await foreach (var daemonEvent in channel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false)) {
                yield return daemonEvent;
            }
        } finally {
            _subscribers.TryRemove(subscriberId, out _);
            channel.Writer.TryComplete();
            _logger.LogInformation("Broadcast subscriber {Id} removed", subscriberId);
        }
    }

    public void BroadcastRuleChange(FirewallRule rule, Local.FirewallRuleChange.Types.ChangeKind kind) {
        ArgumentNullException.ThrowIfNull(rule);
        var change = new Local.FirewallRuleChange {
            Change = kind,
            Rule = rule.ToProto(),
        };
        FanOut(new Local.DaemonEvent { RuleChange = change });
    }

    /// <summary>
    /// Broadcasts <paramref name="alert"/> as a <c>DaemonEvent.Alert</c>
    /// (an <c>AlertEvent</c> wrapper) to every subscribed UI client. Phase
    /// 7's detectors (<c>NewProcessDetector</c>, <c>BinaryHashMonitor</c>,
    /// <c>ChainIntegrityMonitor</c>) call this after appending the alert's
    /// chain row via <see cref="IEventStore.AppendAsync"/>. The chain row
    /// is the durable record; the broadcast is best-effort live UI update.
    /// </summary>
    public void BroadcastAlert(Alert alert) {
        ArgumentNullException.ThrowIfNull(alert);
        var alertEvent = new Local.AlertEvent { Alert = alert.ToProto() };
        FanOut(new Local.DaemonEvent { Alert = alertEvent });
    }

    /// <summary>
    /// Broadcasts a Phase 9.2 LAN scanner "new device" observation. Called by
    /// <c>LanScannerService.ProcessObservationAsync</c> after the matching
    /// <see cref="EventKind.LanDeviceFirstSeen"/> chain row is appended via
    /// <see cref="IEventStore.AppendAsync"/>. The chain row is the durable
    /// record; the broadcast is the best-effort live Scanner-tab update.
    /// </summary>
    public void BroadcastLanDeviceFirstSeen(LanDevice device) {
        ArgumentNullException.ThrowIfNull(device);
        var ev = new Local.LanDeviceFirstSeenEvent { Device = device.ToProto() };
        FanOut(new Local.DaemonEvent { LanDeviceFirstSeen = ev });
    }

    /// <summary>
    /// Broadcasts a Phase 9.2 LAN scanner "MAC changed for a known IP"
    /// observation. <paramref name="previousMac"/> is the MAC that used to
    /// claim <paramref name="device"/>.<see cref="LanDevice.Ip"/>;
    /// <paramref name="device"/> carries the new MAC + the rest of the
    /// observation (vendor, hostname, timestamps).
    /// </summary>
    public void BroadcastLanDeviceMacChanged(string previousMac, LanDevice device) {
        ArgumentException.ThrowIfNullOrEmpty(previousMac);
        ArgumentNullException.ThrowIfNull(device);
        var ev = new Local.LanDeviceMacChangedEvent {
            PreviousMac = previousMac,
            Device = device.ToProto(),
        };
        FanOut(new Local.DaemonEvent { LanDeviceMacChanged = ev });
    }

    /// <summary>
    /// Broadcasts a Phase 9.5 LAN device label change. Called by
    /// <c>BeholderLocalService.SetLanDeviceLabel</c> after the
    /// <see cref="ILanDeviceStore.SetLabelAsync"/> call succeeds and the
    /// updated row is re-fetched. <paramref name="device"/> carries the
    /// refreshed device including the new (or cleared) label.
    /// </summary>
    public void BroadcastLanDeviceLabelChanged(LanDevice device) {
        ArgumentNullException.ThrowIfNull(device);
        var ev = new Local.LanDeviceLabelChangedEvent { Device = device.ToProto() };
        FanOut(new Local.DaemonEvent { LanDeviceLabelChanged = ev });
    }

    private void OnSnapshotBatch(IReadOnlyList<CounterSnapshot> snapshots) {
        var batch = new Local.CounterBatch {
            TickTimestampUnixNs = _timeProvider.GetUtcNow().ToUnixTimeNanoseconds(),
        };
        foreach (var snapshot in snapshots) {
            batch.Snapshots.Add(snapshot.ToProto());
        }
        FanOut(new Local.DaemonEvent { CounterBatch = batch });
    }

    /// <summary>
    /// Writes <paramref name="daemonEvent"/> to every subscriber's bounded
    /// channel. <see cref="BoundedChannelFullMode.DropOldest"/> means
    /// <see cref="ChannelWriter{T}.TryWrite"/> only returns false on a
    /// completed writer (subscriber disconnected between the dictionary
    /// snapshot and this call — benign, just skip).
    /// </summary>
    private void FanOut(Local.DaemonEvent daemonEvent) {
        foreach (var (_, channel) in _subscribers) {
            channel.Writer.TryWrite(daemonEvent);
        }
    }
}
