namespace Beholder.Core;

/// <summary>
/// Source of <see cref="StorageStats"/> for the Settings tab's Data Storage
/// section. The boundary lives in Core (rather than directly in the gRPC
/// service) so the RPC handler can be unit-tested against a fake provider
/// without touching real SQLite.
/// </summary>
public interface IStorageStatsProvider {
    /// <summary>
    /// Returns a fresh snapshot. Implementations should compute per-table
    /// row counts on each call (SQLite <c>COUNT(*)</c> on indexed tables is
    /// fast enough that caching would add complexity without meaningful
    /// payoff).
    /// </summary>
    Task<StorageStats> GetAsync(CancellationToken cancellationToken);
}
