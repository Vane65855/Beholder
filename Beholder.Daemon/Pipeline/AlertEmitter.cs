using Beholder.Core;
using Beholder.Daemon.Storage;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Default <see cref="IAlertEmitter"/>. Encodes the alert payload with
/// <see cref="AlertPayloadEncoder"/>, appends a chain row via
/// <see cref="IEventStore.AppendAsync"/>, then fans the resulting
/// <see cref="Alert"/> out to subscribed UI clients via
/// <see cref="BroadcastService.BroadcastAlert"/>.
/// </summary>
/// <remarks>
/// The chain append is the only failure mode that actually loses data —
/// if the broadcast throws, the persisted alert still surfaces on the next
/// UI snapshot fetch. The broadcast call is therefore wrapped in a
/// try/catch so a flaky broadcast can never turn into a missed audit row.
/// </remarks>
internal sealed class AlertEmitter : IAlertEmitter {
    private readonly IEventStore _eventStore;
    private readonly BroadcastService _broadcaster;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AlertEmitter> _logger;

    public AlertEmitter(
        IEventStore eventStore,
        BroadcastService broadcaster,
        TimeProvider timeProvider,
        ILogger<AlertEmitter> logger
    ) {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _eventStore = eventStore;
        _broadcaster = broadcaster;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<long> EmitAlertAsync(
        AlertKind kind,
        string processPath,
        string summary,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(processPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        var eventKind = MapToEventKind(kind);
        var payload = AlertPayloadEncoder.Encode(processPath, summary);

        var seq = await _eventStore.AppendAsync(eventKind, payload, cancellationToken).ConfigureAwait(false);
        var alert = new Alert(
            seq: seq,
            kind: kind,
            processPath: processPath,
            summary: summary,
            timestamp: _timeProvider.GetUtcNow(),
            firstViewedAt: null);

        try {
            _broadcaster.BroadcastAlert(alert);
        } catch (Exception ex) {
            // Chain row is durable; the live broadcast is best-effort. UI
            // will pick the alert up on its next snapshot fetch.
            _logger.LogError(ex, "Failed to broadcast alert seq {Seq} ({Kind})", seq, kind);
        }

        return seq;
    }

    /// <summary>
    /// One-to-one map from <see cref="AlertKind"/> to <see cref="EventKind"/>.
    /// The two enums diverge on numeric ordinals — they are deliberately not
    /// declared as a single enum, so a switch is the contract.
    /// </summary>
    private static EventKind MapToEventKind(AlertKind kind) => kind switch {
        AlertKind.NewProcess => EventKind.NewProcess,
        AlertKind.HashChanged => EventKind.HashChanged,
        AlertKind.ChainError => EventKind.ChainError,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, $"Cannot emit alert with kind {kind}"),
    };
}
