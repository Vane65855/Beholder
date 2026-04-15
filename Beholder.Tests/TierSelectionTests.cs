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
    public void Select_VeryFineResolutionLongRange_FallsBackToFinestCoveringTier() {
        // Range = 2 years, resolution = 1s. No tier has a bucket size ≤ 1s
        // AND retention ≥ 2 years. Fallback rule: return the finest tier whose
        // retention covers the range. Under Balanced, _10m.Retention = 365d
        // (fails) and _1h.Retention = null (infinite, passes).
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddYears(-2),
            resolution: TimeSpan.FromSeconds(1),
            now: Now);
        Assert.Equal("traffic_buckets_1h", tier.TableName);
    }

    [Fact]
    public void Select_FineResolutionMidRange_FallsBackToFinestCoveringTier() {
        // Range = 60 days, resolution = 1s. No tier has bucket ≤ 1s AND
        // retention ≥ 60d under Balanced (_10s=7d, _1m=14d). Fallback: finest
        // tier covering 60 days → _10m (365d retention, first covering tier
        // in finest-to-coarsest order).
        var tier = TierSelector.Select(
            BalancedTiers,
            from: Now.AddDays(-60),
            resolution: TimeSpan.FromSeconds(1),
            now: Now);
        Assert.Equal("traffic_buckets_10m", tier.TableName);
    }
}
