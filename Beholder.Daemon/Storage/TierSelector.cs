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
    /// less than or equal to the requested <paramref name="resolution"/>, AND
    /// whose <see cref="RollupTier.Retention"/> covers the range
    /// <c>[from, now]</c>. Ties broken toward the coarser tier (fewer rows
    /// scanned).
    /// </summary>
    /// <remarks>
    /// Fallback rules when no tier satisfies both constraints:
    /// <list type="number">
    /// <item>Return the finest tier whose retention covers the range. The
    /// returned tier's <see cref="RollupTier.BucketSeconds"/> may be coarser
    /// than <paramref name="resolution"/>, so the caller receives data at the
    /// tier's native bucket size, not at the requested resolution.</item>
    /// <item>If no tier's retention covers the range (either all tiers have
    /// shorter retention, or a tier has <c>null</c> retention which is treated
    /// as infinite and always covers), return the last tier in the list.
    /// Terminal tiers with <c>null</c> retention always match the covers-range
    /// check.</item>
    /// </list>
    /// </remarks>
    public static RollupTier Select(
        IReadOnlyList<RollupTier> tiers,
        DateTimeOffset from,
        TimeSpan resolution,
        DateTimeOffset now
    ) {
        ArgumentNullException.ThrowIfNull(tiers);
        if (tiers.Count == 0) throw new ArgumentException("Tier list cannot be empty.", nameof(tiers));

        var range = now - from;
        if (range < TimeSpan.Zero) range = TimeSpan.Zero;

        RollupTier? best = null;
        foreach (var tier in tiers) {
            if (tier.BucketSeconds > resolution.TotalSeconds) continue;
            if (!TierCoversRange(tier, range)) continue;
            if (best is null || tier.BucketSeconds > best.BucketSeconds) best = tier;
        }
        if (best is not null) return best;

        // No tier matches both the resolution and range constraints. Fall back
        // to the finest tier whose retention covers the range. Iterated in
        // finest-to-coarsest order (the list's native order).
        foreach (var tier in tiers) {
            if (TierCoversRange(tier, range)) return tier;
        }

        // Shouldn't happen when a null-retention terminal tier exists, but
        // handle it defensively: return the last tier.
        return tiers[^1];
    }

    private static bool TierCoversRange(RollupTier tier, TimeSpan range) =>
        tier.Retention is null || tier.Retention.Value >= range;
}
