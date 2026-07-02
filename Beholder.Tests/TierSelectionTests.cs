using Beholder.Daemon;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public class TierSelectionTests {
    private static readonly DateTimeOffset Now =
        new(2026, 4, 15, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<RollupTier> BalancedTiers =>
        new RollupOptions { Preset = RetentionPreset.Balanced }.Tiers;

    [Fact]
    public void Select_LiveRange_PicksRaw() {
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddMinutes(-2),
            resolution: TimeSpan.FromSeconds(1),
            now: Now);
        Assert.Equal("traffic_raw", tier.TableName);
    }

    [Fact]
    public void Select_RecentRangeCoarseResolution_Picks10s() {
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddMinutes(-2),
            resolution: TimeSpan.FromSeconds(30),
            now: Now);
        Assert.Equal("traffic_buckets_10s", tier.TableName);
    }

    [Fact]
    public void Select_MediumRange_Picks1m() {
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddHours(-24),
            resolution: TimeSpan.FromMinutes(5),
            now: Now);
        Assert.Equal("traffic_buckets_1m", tier.TableName);
    }

    [Fact]
    public void Select_LongRange_Picks10m() {
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddDays(-30),
            resolution: TimeSpan.FromMinutes(15),
            now: Now);
        Assert.Equal("traffic_buckets_10m", tier.TableName);
    }

    [Fact]
    public void Select_HistoricalRange_Picks1h() {
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddDays(-180),
            resolution: TimeSpan.FromHours(1),
            now: Now);
        Assert.Equal("traffic_buckets_1h", tier.TableName);
    }

    [Fact]
    public void Select_CoarseResolutionShortRange_PicksCoarsest() {
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddMinutes(-5),
            resolution: TimeSpan.FromHours(1),
            now: Now);
        Assert.Equal("traffic_buckets_1h", tier.TableName);
    }

    [Fact]
    public void Select_RangeBeyondAllRetentions_FallsBackToTerminal() {
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddYears(-10),
            resolution: TimeSpan.FromHours(1),
            now: Now);
        Assert.Equal("traffic_buckets_1h", tier.TableName);
    }

    [Fact]
    public void Select_VeryFineResolutionLongRange_PicksFinestCoveringTier() {
        // A 2-year range asked at 1s resolution. Raw (10 min retention) has
        // pruned virtually all of the range — serving it would return only
        // the last 10 minutes and silently drop two years (ADR 017). The
        // finest tier whose retention covers `from` is _1h (null = infinite);
        // the summary sums are identical wherever tiers overlap (rollup
        // invariant), so completeness costs nothing.
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddYears(-2),
            resolution: TimeSpan.FromSeconds(1),
            now: Now);
        Assert.Equal("traffic_buckets_1h", tier.TableName);
    }

    [Fact]
    public void Select_FineResolutionMidRange_PicksFinestCoveringTier() {
        // 60 days back at 1s resolution: raw/_10s/_1m have all pruned the
        // range's start; _10m (365 d) is the finest tier that still covers it.
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddDays(-60),
            resolution: TimeSpan.FromSeconds(1),
            now: Now);
        Assert.Equal("traffic_buckets_10m", tier.TableName);
    }

    // --- SelectTierForAge tests (used by stitched multi-tier queries) ---

    [Fact]
    public void SelectTierForAge_VeryRecent_PicksRaw() {
        var tier = TierSelector.SelectTierForAge(BalancedTiers, TimeSpan.FromMinutes(5));
        Assert.Equal("traffic_raw", tier.TableName);
    }

    [Fact]
    public void SelectTierForAge_LessThanWeek_PicksTenS() {
        var tier = TierSelector.SelectTierForAge(BalancedTiers, TimeSpan.FromDays(3));
        Assert.Equal("traffic_buckets_10s", tier.TableName);
    }

    [Fact]
    public void SelectTierForAge_LessThanTwoWeeks_PicksOneM() {
        var tier = TierSelector.SelectTierForAge(BalancedTiers, TimeSpan.FromDays(10));
        Assert.Equal("traffic_buckets_1m", tier.TableName);
    }

    [Fact]
    public void SelectTierForAge_LessThanYear_PicksTenM() {
        var tier = TierSelector.SelectTierForAge(BalancedTiers, TimeSpan.FromDays(180));
        Assert.Equal("traffic_buckets_10m", tier.TableName);
    }

    [Fact]
    public void SelectTierForAge_BeyondAllFiniteRetentions_PicksOneH() {
        // _1h has null retention (infinite), so it covers any age.
        var tier = TierSelector.SelectTierForAge(BalancedTiers, TimeSpan.FromDays(1000));
        Assert.Equal("traffic_buckets_1h", tier.TableName);
    }

    [Fact]
    public void SelectTierForAge_ZeroAge_PicksFinest() {
        var tier = TierSelector.SelectTierForAge(BalancedTiers, TimeSpan.Zero);
        Assert.Equal("traffic_raw", tier.TableName);
    }

    [Fact]
    public void Select_AllTimeCoarseResolution_PicksCoveringTerminalTier() {
        // "All Time" (from = epoch) at 10-minute resolution. _10m matches the
        // resolution but its 365-day retention has pruned everything older —
        // an "All Time" summary served from it would silently omit year-2+
        // history. Only _1h (∞ retention) covers the range, and the rollup
        // invariant makes its sums identical to _10m's over the shared year,
        // so the coarser tier is pure completeness gain. (Chart-shape
        // consistency across ranges is the stitched timeline path's job, not
        // this selector's — timelines stopped using Select in Phase 4.6b.)
        var tier = TierSelector.Select(
            BalancedTiers,
            from: DateTimeOffset.UnixEpoch,
            resolution: TimeSpan.FromMinutes(10),
            now: Now);
        Assert.Equal("traffic_buckets_1h", tier.TableName);
    }

    // --- ADR 017: small windows anywhere in history (chart selection) ---

    [Fact]
    public void Select_SmallWindowThreeDaysBack_Picks10s() {
        // A 30-minute selection window on a 7-day chart: pseudo-resolution is
        // ~6s, which only raw satisfies — but raw pruned the window days ago.
        // The finest tier still covering the window's age is _10s.
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddDays(-3),
            resolution: TimeSpan.FromSeconds(6),
            now: Now);
        Assert.Equal("traffic_buckets_10s", tier.TableName);
    }

    [Fact]
    public void Select_SmallWindowTenDaysBack_Picks1m() {
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddDays(-10),
            resolution: TimeSpan.FromSeconds(6),
            now: Now);
        Assert.Equal("traffic_buckets_1m", tier.TableName);
    }

    [Fact]
    public void Select_SmallWindowBeyondAllFiniteRetentions_PicksTerminal() {
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddYears(-5),
            resolution: TimeSpan.FromSeconds(6),
            now: Now);
        Assert.Equal("traffic_buckets_1h", tier.TableName);
    }
}
