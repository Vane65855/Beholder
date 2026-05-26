namespace Beholder.Core;

/// <summary>
/// Persistence and query interface for historical per-destination traffic data.
/// The store writes raw 1-second buckets (via <see cref="WriteRawBucketsAsync"/>)
/// and serves tier-aware aggregated queries for timeline charts, destination
/// breakdowns, and geographic analysis.
/// </summary>
/// <remarks>
/// <para>
/// Tier-aware queries: the store picks the coarsest rollup tier whose bucket
/// size is ≤ the requested resolution AND whose retention covers the requested
/// range. Callers do not need to know about tiering — query method signatures
/// accept a range and resolution, and the store routes internally.
/// </para>
/// <para>
/// Rollup invariant: queries that aggregate across tiers MUST preserve additive
/// correctness. <c>SUM(bytes)</c> over a time range returns identical totals
/// regardless of which tier(s) are consulted, because coarser tiers are built
/// by summing finer tiers. See <c>docs/ARCHITECTURE.md</c> "Storage Rollup
/// Architecture" for the invariant's precise statement and the tier-selection
/// rule's full behavior, including fallback cases.
/// </para>
/// <para>
/// Retention pruning is not the store's responsibility. The rollup service
/// owns the per-tier prune schedule (null-retention tiers are never pruned).
/// </para>
/// </remarks>
public interface ITrafficStore {
    /// <summary>
    /// Persists a batch of per-destination 1-second raw buckets in a single
    /// transaction. These rows land in <c>traffic_raw</c> and are cascaded
    /// into coarser tiers by the rollup service.
    /// </summary>
    Task WriteRawBucketsAsync(IReadOnlyList<TrafficBucket> buckets, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a time series of traffic for a single process, re-aggregated into
    /// intervals of <paramref name="resolution"/>. Each point contains the sum of
    /// bytes in/out for all destinations of this process within that interval.
    /// The store picks the most efficient tier internally based on
    /// <paramref name="from"/>, <paramref name="to"/>, and <paramref name="resolution"/>.
    /// </summary>
    Task<IReadOnlyList<TrafficTimePoint>> GetProcessTimelineAsync(
        string processPath,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan resolution,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns all distinct destinations contacted in a time range, with
    /// aggregated byte totals and connection (distinct port) counts. When
    /// <paramref name="processPath"/> is non-null, results are filtered to
    /// that single process; when null, destinations aggregate across every
    /// process in the range. Tier selection is retention-only (no resolution
    /// parameter).
    /// </summary>
    Task<IReadOnlyList<DestinationSummary>> GetDestinationsAsync(
        DestinationsQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns a time series of traffic across all processes, re-aggregated into
    /// intervals of <paramref name="resolution"/>. Tier selection matches
    /// <see cref="GetProcessTimelineAsync"/>.
    /// </summary>
    Task<IReadOnlyList<TrafficTimePoint>> GetAggregateTimelineAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan resolution,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns all distinct processes with traffic in the given time range,
    /// with aggregated byte totals. Unlike <c>GetSnapshot</c> (which only
    /// returns processes the engine currently tracks in memory), this query
    /// hits the tiered storage so historically-active processes appear even
    /// after the engine has evicted them.
    /// </summary>
    /// <summary>
    /// When <paramref name="remoteAddress"/> is non-null and non-empty, the
    /// aggregation is restricted to traffic whose <c>remote_address</c> column
    /// equals that value (Phase 9.6 — backs the Scanner → Traffic cross-link).
    /// Null or empty = no filter, behaves identically to the pre-9.6 contract.
    /// </summary>
    Task<IReadOnlyList<ProcessTrafficSummary>> GetProcessSummariesAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken,
        string? remoteAddress = null);

    /// <summary>
    /// Returns per-country traffic totals for a time range. When
    /// <paramref name="processPath"/> is non-null, results are filtered to
    /// that single process; when null, totals aggregate across every process
    /// in the range. Tier selection is retention-only.
    /// </summary>
    Task<IReadOnlyList<CountryTrafficSummary>> GetCountryBreakdownAsync(
        string? processPath,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns per-protocol traffic totals for a time range, derived from
    /// remote port via the daemon's port→name classification. When
    /// <paramref name="processPath"/> is non-null, results are filtered to
    /// that single process; when null, totals aggregate across every process
    /// in the range. Tier selection is retention-only.
    /// </summary>
    Task<IReadOnlyList<ProtocolBreakdownSummary>> GetProtocolBreakdownAsync(
        string? processPath,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);
}
