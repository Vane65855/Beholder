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
    /// less than or equal to the requested <paramref name="resolution"/>,
    /// considering only tiers whose retention still covers
    /// <paramref name="from"/>. Ties broken toward the coarser tier (fewer
    /// rows scanned).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retention gates eligibility: a tier whose retention window starts after
    /// <paramref name="from"/> has already pruned (part of) the requested
    /// range, so serving from it silently drops the range's older portion.
    /// That was tolerable when every caller's range ended at <em>now</em> (the
    /// preset views), but the chart-selection feature (ADR 017) issues small
    /// windows anywhere in history — a 30-minute window three days ago must
    /// not be served from <c>traffic_raw</c>'s 10-minute retention, which
    /// would return a guaranteed-empty result while <c>_10s</c> holds the
    /// data. Null retention means infinite and always covers.
    /// </para>
    /// <para>
    /// Fallback when no eligible tier has <c>BucketSeconds ≤ resolution</c>:
    /// the finest eligible tier — data at coarser-than-requested granularity
    /// beats a guaranteed-empty finer tier. If no tier covers
    /// <paramref name="from"/> at all (only possible if the terminal tier has
    /// finite retention), the terminal tier, mirroring
    /// <see cref="SelectTierForAge"/>'s defensive fallback.
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
        RollupTier? finestEligible = null;
        foreach (var tier in tiers) {
            var covers = tier.Retention is null || now - tier.Retention.Value <= from;
            if (!covers) continue;
            finestEligible ??= tier;
            if (tier.BucketSeconds > resolution.TotalSeconds) continue;
            if (best is null || tier.BucketSeconds > best.BucketSeconds) best = tier;
        }
        if (best is not null) return best;
        if (finestEligible is not null) return finestEligible;

        return tiers[^1];
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
