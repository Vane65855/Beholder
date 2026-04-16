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
    public void Select_VeryFineResolutionLongRange_PicksRaw() {
        // Resolution = 1s picks the tier with bucket ≤ 1s regardless of range.
        // The SQL WHERE clause naturally returns only rows that exist, so the
        // caller gets raw-resolution data for whatever recent portion of the
        // range the raw tier retains (10 min under Balanced), and empty rows
        // for the older portion. This is strictly better than returning _1h
        // (coarse) just because retention covers the whole range.
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddYears(-2),
            resolution: TimeSpan.FromSeconds(1),
            now: Now);
        Assert.Equal("traffic_raw", tier.TableName);
    }

    [Fact]
    public void Select_FineResolutionMidRange_PicksRaw() {
        // Same rule as above: resolution=1s picks raw, independent of range.
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddDays(-60),
            resolution: TimeSpan.FromSeconds(1),
            now: Now);
        Assert.Equal("traffic_raw", tier.TableName);
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
    public void Select_AllTimeCoarseResolution_PicksFinerTierWhenAvailable() {
        // "All Time" with a 56-year range and 10-minute resolution. Under the
        // old retention-gated rule, only _1h (∞ retention) qualified. Under the
        // new rule, _10m (bucket=600s ≤ 600s resolution) is the coarsest match
        // and wins. This is the fix for "All Time shows different chart shape
        // than Last 30 Days" — both views now use the same tier for the data
        // they actually share.
        var tier = TierSelector.Select(
            BalancedTiers,
            from: DateTimeOffset.UnixEpoch,
            resolution: TimeSpan.FromMinutes(10),
            now: Now);
        Assert.Equal("traffic_buckets_10m", tier.TableName);
    }
}
