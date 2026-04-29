using Beholder.Core;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Facade combining <see cref="IEventStore.AppendAsync"/> and
/// <see cref="BroadcastService.BroadcastAlert"/> into a single call so the
/// Phase 7 detectors don't juggle both. The chain row is the durable record;
/// the broadcast is best-effort live UI delivery (slow subscribers drop old
/// events per <see cref="BroadcastService"/> semantics).
/// </summary>
internal interface IAlertEmitter {
    /// <summary>
    /// Append an alert row to the chain and broadcast it live to subscribed
    /// UI clients. Returns the chain seq assigned to the appended row so
    /// callers (e.g. detectors that want to log "emitted alert seq N") can
    /// reference it without a second database round-trip.
    /// </summary>
    Task<long> EmitAlertAsync(
        AlertKind kind,
        string processPath,
        string summary,
        CancellationToken cancellationToken);
}
