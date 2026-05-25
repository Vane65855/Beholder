using Beholder.Protocol.Local;
using Beholder.Ui.ViewModels;

namespace Beholder.Tests;

public class TableStatsRowTests {
    [Fact]
    public void FromProto_PopulatesName_AndFormatsRowCount() {
        var row = TableStatsRow.FromProto(maxRowCountInGroup: 0, proto: new TableStats {
            Name = "traffic_raw", RowCount = 1234567,
        });

        Assert.Equal("traffic_raw", row.Name);
        Assert.Equal(1234567, row.RowCount);
        Assert.Equal("1,234,567", row.RowCountFormatted);
        Assert.True(row.IsTrafficTier);
    }

    [Theory]
    [InlineData("traffic_raw", true)]
    [InlineData("traffic_buckets_10s", true)]
    [InlineData("traffic_buckets_1m", true)]
    [InlineData("traffic_buckets_10m", true)]
    [InlineData("traffic_buckets_1h", true)]
    [InlineData("event_log", false)]
    [InlineData("lan_device", false)]
    [InlineData("dns_cache", false)]
    public void IsTrafficTier_RecognisesTheFiveCascadeTiers(string name, bool expected) {
        var row = TableStatsRow.FromProto(maxRowCountInGroup: 0, proto: new TableStats { Name = name, RowCount = 0 });
        Assert.Equal(expected, row.IsTrafficTier);
    }

    [Fact]
    public void FromProto_NullProto_Throws() {
        Assert.Throws<ArgumentNullException>(() => TableStatsRow.FromProto(null!, 0));
    }

    [Fact]
    public void DisplayName_KnownTable_UsesPrettyName() {
        var row = TableStatsRow.FromProto(new TableStats { Name = "traffic_raw", RowCount = 0 }, 0);
        Assert.Equal("Traffic — raw (1s)", row.DisplayName);
    }

    [Fact]
    public void DisplayName_UnknownTable_FallsBackToRawName() {
        var row = TableStatsRow.FromProto(new TableStats { Name = "future_table", RowCount = 0 }, 0);
        Assert.Equal("future_table", row.DisplayName);
        Assert.Equal("—", row.Retention);
        Assert.Equal(999, row.SortKey);
    }

    [Theory]
    [InlineData("traffic_raw", "10 min", 0)]
    [InlineData("traffic_buckets_10s", "7 days", 1)]
    [InlineData("traffic_buckets_1m", "14 days", 2)]
    [InlineData("traffic_buckets_10m", "1 year", 3)]
    [InlineData("traffic_buckets_1h", "∞", 4)]
    [InlineData("event_log", "∞", 103)]
    public void TrafficCascadeMetadata_RetentionAndSortKey(string name, string expectedRetention, int expectedSortKey) {
        var row = TableStatsRow.FromProto(new TableStats { Name = name, RowCount = 0 }, 0);
        Assert.Equal(expectedRetention, row.Retention);
        Assert.Equal(expectedSortKey, row.SortKey);
    }

    [Fact]
    public void RowCountShareRatio_ScaledRelativeToMax() {
        var row = TableStatsRow.FromProto(
            new TableStats { Name = "traffic_raw", RowCount = 500 }, maxRowCountInGroup: 1000);
        Assert.Equal(0.5, row.RowCountShareRatio);
    }

    [Fact]
    public void RowCountShareRatio_ZeroMax_YieldsZero() {
        var row = TableStatsRow.FromProto(
            new TableStats { Name = "traffic_raw", RowCount = 0 }, maxRowCountInGroup: 0);
        Assert.Equal(0.0, row.RowCountShareRatio);
    }
}
