using Beholder.Protocol.Local;
using Beholder.Ui.ViewModels;

namespace Beholder.Tests;

public class TableStatsRowTests {
    [Fact]
    public void FromProto_PopulatesName_AndFormatsRowCount() {
        var row = TableStatsRow.FromProto(new TableStats {
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
        var row = TableStatsRow.FromProto(new TableStats { Name = name, RowCount = 0 });
        Assert.Equal(expected, row.IsTrafficTier);
    }

    [Fact]
    public void FromProto_NullProto_Throws() {
        Assert.Throws<ArgumentNullException>(() => TableStatsRow.FromProto(null!));
    }
}
