namespace Beholder.Core;

/// <summary>
/// The chain-hashed append-only event log. The single point of truth for any state
/// change that needs tamper evidence — counters, alerts, firewall rule mutations.
/// Implementations are responsible for computing each row's hash from the canonical
/// payload bytes plus the previous row's hash, and for never deleting or updating
/// existing rows.
/// </summary>
public interface IEventStore {
    /// <summary>
    /// Appends an event to the log. The implementation computes the chain hash from
    /// <paramref name="payload"/> and the most recent row's hash, then writes the new
    /// row atomically. Callers must supply the canonical byte representation of the
    /// event — the store does not serialize.
    /// </summary>
    Task AppendAsync(EventKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    /// <summary>
    /// Walks the entire chain from the first row to the last, recomputing each row's
    /// hash and comparing it against the stored value. Side-effect-free and idempotent
    /// — running it twice produces identical results.
    /// </summary>
    Task<ChainVerificationResult> VerifyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the most recent alerts in newest-first order, capped at
    /// <paramref name="limit"/> entries. Used by the UI's <c>GetSnapshot</c> RPC to
    /// populate the alert list on connect.
    /// </summary>
    Task<IReadOnlyList<Alert>> GetAlertsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Marks an alert as read by setting its <see cref="Alert.FirstViewedAt"/>.
    /// Idempotent — if the alert is already marked read, the existing timestamp is
    /// preserved and the call succeeds without modification.
    /// </summary>
    Task MarkAlertReadAsync(long seq, DateTimeOffset viewedAt, CancellationToken cancellationToken);
}
