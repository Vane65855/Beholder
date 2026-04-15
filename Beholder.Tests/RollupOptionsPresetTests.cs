using Beholder.Daemon;

namespace Beholder.Tests;

public class RollupOptionsPresetTests {
    [Fact]
    public void Default_IsBalancedPreset() {
        var options = new RollupOptions();
        Assert.Equal(RetentionPreset.Balanced, options.Preset);
    }

    [Fact]
    public void Balanced_HasExpectedTierShape() {
        var options = new RollupOptions { Preset = RetentionPreset.Balanced };

        Assert.Equal(5, options.Tiers.Count);
        Assert.Equal(new[] { "traffic_raw", "traffic_buckets_10s", "traffic_buckets_1m", "traffic_buckets_10m", "traffic_buckets_1h" },
            options.Tiers.Select(t => t.TableName).ToArray());
        Assert.Equal(new[] { 1, 10, 60, 600, 3600 },
            options.Tiers.Select(t => t.BucketSeconds).ToArray());

        var retentions = options.Tiers.Select(t => t.Retention).ToArray();
        Assert.Equal(TimeSpan.FromMinutes(10), retentions[0]);
        Assert.Equal(TimeSpan.FromDays(7), retentions[1]);
        Assert.Equal(TimeSpan.FromDays(14), retentions[2]);
        Assert.Equal(TimeSpan.FromDays(365), retentions[3]);
        Assert.Null(retentions[4]);
    }

    [Fact]
    public void Compact_HasExpectedTierShape() {
        var options = new RollupOptions { Preset = RetentionPreset.Compact };

        Assert.Equal(5, options.Tiers.Count);
        Assert.Equal(new[] { "traffic_raw", "traffic_buckets_10s", "traffic_buckets_1m", "traffic_buckets_10m", "traffic_buckets_1h" },
            options.Tiers.Select(t => t.TableName).ToArray());
        Assert.Equal(new[] { 1, 10, 60, 600, 3600 },
            options.Tiers.Select(t => t.BucketSeconds).ToArray());

        var retentions = options.Tiers.Select(t => t.Retention).ToArray();
        Assert.Equal(TimeSpan.FromMinutes(10), retentions[0]);
        Assert.Equal(TimeSpan.FromDays(3), retentions[1]);
        Assert.Equal(TimeSpan.FromDays(7), retentions[2]);
        Assert.Equal(TimeSpan.FromDays(90), retentions[3]);
        Assert.Null(retentions[4]);
    }

    [Fact]
    public void Preset_SwitchesBetweenTierLists() {
        var options = new RollupOptions { Preset = RetentionPreset.Compact };
        Assert.Equal(TimeSpan.FromDays(3), options.Tiers[1].Retention);

        options.Preset = RetentionPreset.Balanced;
        Assert.Equal(TimeSpan.FromDays(7), options.Tiers[1].Retention);

        options.Preset = RetentionPreset.Compact;
        Assert.Equal(TimeSpan.FromDays(3), options.Tiers[1].Retention);
    }

    [Fact]
    public void TerminalTier_BothPresets_HasNullRetention() {
        var balanced = new RollupOptions { Preset = RetentionPreset.Balanced };
        var compact = new RollupOptions { Preset = RetentionPreset.Compact };

        Assert.Null(balanced.Tiers[^1].Retention);
        Assert.Null(compact.Tiers[^1].Retention);
        Assert.Equal(TimeSpan.Zero, balanced.Tiers[^1].RollupInterval);
        Assert.Equal(TimeSpan.Zero, compact.Tiers[^1].RollupInterval);
    }

    [Fact]
    public void BothPresets_TierCountIsFive() {
        Assert.Equal(5, new RollupOptions { Preset = RetentionPreset.Balanced }.Tiers.Count);
        Assert.Equal(5, new RollupOptions { Preset = RetentionPreset.Compact }.Tiers.Count);
    }

    [Fact]
    public void BothPresets_BucketSecondsIdentical() {
        var balanced = new RollupOptions { Preset = RetentionPreset.Balanced };
        var compact = new RollupOptions { Preset = RetentionPreset.Compact };

        Assert.Equal(
            balanced.Tiers.Select(t => t.BucketSeconds).ToArray(),
            compact.Tiers.Select(t => t.BucketSeconds).ToArray());
    }
}
