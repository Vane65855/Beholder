namespace Beholder.Core;

/// <summary>
/// Persistent storage for DNS hostname-to-IP mappings observed by the platform DNS
/// observer. Unlike <see cref="IDnsCache"/> (which is in-memory and lost on restart),
/// this store survives daemon restarts and can be queried to backfill hostnames on
/// traffic records that were written before the DNS response arrived.
/// </summary>
public interface IDnsCacheStore {
    /// <summary>
    /// Upserts a batch of DNS mappings in a single transaction. If an address already
    /// exists, its hostname and timestamp are updated.
    /// </summary>
    Task UpsertBatchAsync(
        IReadOnlyList<(string Address, string Hostname)> entries,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the most recently stored hostname for the given address string, or
    /// <c>null</c> if no mapping exists.
    /// </summary>
    Task<string?> ResolveAsync(string address, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes DNS entries not updated since <paramref name="cutoff"/>. Returns
    /// the number of rows deleted.
    /// </summary>
    Task<long> PruneAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
