namespace Beholder.Core;

/// <summary>
/// Persistence and query interface for historical per-destination traffic data.
/// Implementations store traffic in time-bucketed rows and serve aggregated queries
/// for timeline charts, destination breakdowns, and geographic analysis.
///
/// Phase 4.6a: all queries hit the single <c>traffic_buckets_10s</c> table.
/// Phase 4.6b/c will add tier-selection logic based on requested time range and
/// resolution, routing queries to the most efficient tier.
///
/// Rollup invariant: queries that aggregate across tiers MUST preserve additive
/// correctness. <c>SUM(bytes)</c> over a time range returns identical totals
/// regardless of which tier(s) are consulted, because coarser tiers are built by
/// summing finer tiers. See <c>docs/ARCHITECTURE.md</c> "Storage Rollup
/// Architecture" for details.
/// </summary>
public interface ITrafficStore {
    /// <summary>Persists a batch of micro-aggregated traffic buckets in a single transaction.</summary>
    Task WriteBucketsAsync(IReadOnlyList<TrafficBucket> buckets, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a time series of traffic for a single process, re-aggregated into
    /// intervals of <paramref name="resolution"/>. Each point contains the sum of
    /// bytes in/out for all destinations of this process within that interval.
    /// </summary>
    Task<IReadOnlyList<TrafficTimePoint>> GetProcessTimelineAsync(
        string processPath,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan resolution,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns all distinct destinations contacted by a process in a time range,
    /// with aggregated byte totals and connection (distinct port) counts.
    /// </summary>
    Task<IReadOnlyList<DestinationSummary>> GetProcessDestinationsAsync(
        string processPath,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns a time series of traffic across all processes, re-aggregated into
    /// intervals of <paramref name="resolution"/>.
    /// </summary>
    Task<IReadOnlyList<TrafficTimePoint>> GetAggregateTimelineAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan resolution,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns per-country traffic totals for a time range, suitable for the map
    /// tab's geographic heat map.
    /// </summary>
    Task<IReadOnlyList<CountryTrafficSummary>> GetCountryBreakdownAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes traffic data older than <paramref name="cutoff"/>. Returns the
    /// number of rows deleted. Idempotent.
    /// </summary>
    Task<long> PruneAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
