namespace Beholder.Daemon.Storage;

/// <summary>
/// Chooses which rollup tier to serve a historical traffic query from, based on
/// the requested time range and resolution. Pure function: no side effects, no
/// I/O, deterministic from its inputs. Extracted as a static helper so the rule
/// is unit-testable without pipeline plumbing, and so <see cref="SqliteTrafficStore"/>
/// has one narrow dependency to mock in its own tests.
/// </summary>
internal static class TierSelector {
    /// <summary>
    /// Picks the coarsest tier whose <see cref="RollupTier.BucketSeconds"/> is
    /// less than or equal to the requested <paramref name="resolution"/>. Ties
    /// broken toward the coarser tier (fewer rows scanned).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retention is NOT used to filter tiers. A tier's retention cap limits how
    /// far back that tier has data — but <c>WHERE bucket_start_ms &gt;= from</c>
    /// naturally returns only the rows that actually exist, so querying a
    /// shorter-retention tier for a longer range just yields whatever data the
    /// tier has (typically the most recent portion of the range at full
    /// fidelity). This is strictly better than falling back to a coarser tier
    /// that happens to have infinite retention: the user wants the finest
    /// granularity available for whatever data the daemon has, not the coarsest
    /// guaranteed-complete tier.
    /// </para>
    /// <para>
    /// Fallback when no tier has <c>BucketSeconds ≤ resolution</c> (very fine
    /// resolution asked against only-coarse tiers): return the finest tier
    /// (first in the list). The caller receives data at the tier's native
    /// bucket size, not at the requested resolution.
    /// </para>
    /// </remarks>
    public static RollupTier Select(
        IReadOnlyList<RollupTier> tiers,
        DateTimeOffset from,
        TimeSpan resolution,
        DateTimeOffset now
    ) {
        ArgumentNullException.ThrowIfNull(tiers);
        if (tiers.Count == 0) throw new ArgumentException("Tier list cannot be empty.", nameof(tiers));

        RollupTier? best = null;
        foreach (var tier in tiers) {
            if (tier.BucketSeconds > resolution.TotalSeconds) continue;
            if (best is null || tier.BucketSeconds > best.BucketSeconds) best = tier;
        }
        if (best is not null) return best;

        // Requested resolution is finer than any tier's bucket size. Return
        // the finest tier available; caller gets data at that tier's granularity.
        return tiers[0];
    }

    /// <summary>
    /// Picks the finest tier whose retention covers the given age. Used by
    /// stitched multi-tier timeline queries to assign each output bucket to
    /// the most precise tier that still has data for that bucket's age.
    /// Null-retention tiers are treated as infinite and always cover any age.
    /// </summary>
    /// <remarks>
    /// For a 2-year "All Time" query under Balanced, this routes:
    /// <list type="bullet">
    /// <item>age ≤ 10 min → <c>traffic_raw</c></item>
    /// <item>age ≤ 7 days → <c>traffic_buckets_10s</c></item>
    /// <item>age ≤ 14 days → <c>traffic_buckets_1m</c></item>
    /// <item>age ≤ 1 year → <c>traffic_buckets_10m</c></item>
    /// <item>age &gt; 1 year → <c>traffic_buckets_1h</c> (null retention)</item>
    /// </list>
    /// </remarks>
    public static RollupTier SelectTierForAge(
        IReadOnlyList<RollupTier> tiers,
        TimeSpan age
    ) {
        ArgumentNullException.ThrowIfNull(tiers);
        if (tiers.Count == 0) throw new ArgumentException("Tier list cannot be empty.", nameof(tiers));

        foreach (var tier in tiers) {
            if (tier.Retention is null || tier.Retention.Value >= age) return tier;
        }

        // No tier covers the age — defensive fallback to terminal tier.
        return tiers[^1];
    }
}
