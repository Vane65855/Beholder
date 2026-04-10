namespace Beholder.Core;

/// <summary>
/// Read and read-state operations over the alert side of the event log. Alert events
/// themselves are appended to the chain via <see cref="IEventStore.AppendAsync"/>; this
/// interface only exposes the queryable view of those alerts plus their out-of-chain
/// read-state (which lives in a side column or table — read-state is not chained
/// because it would otherwise mutate sealed rows).
/// </summary>
public interface IAlertStore {
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
