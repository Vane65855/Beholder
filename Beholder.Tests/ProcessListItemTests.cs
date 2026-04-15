using Beholder.Ui.Models;

namespace Beholder.Tests;

public class ProcessListItemTests {
    [Fact]
    public void UpdateTraffic_SetsBothDirections() {
        var item = new ProcessListItem("fake/test.exe", "test.exe");

        item.UpdateTraffic(recentBytesIn: 12_345, recentBytesOut: 67_890);

        Assert.Equal(12_345, item.RecentBytesIn);
        Assert.Equal(67_890, item.RecentBytesOut);
    }

    [Fact]
    public void UpdateTraffic_FormatsLabels() {
        var item = new ProcessListItem("fake/test.exe", "test.exe");

        item.UpdateTraffic(recentBytesIn: 2048, recentBytesOut: 1024);

        Assert.Equal("2.0 KB", item.RecentInLabel);
        Assert.Equal("1.0 KB", item.RecentOutLabel);
    }

    [Fact]
    public void SortKey_IsRecentInPlusOut() {
        var item = new ProcessListItem("fake/test.exe", "test.exe");

        item.UpdateTraffic(recentBytesIn: 500, recentBytesOut: 1500);

        Assert.Equal(2000, item.SortKey);
    }

    [Fact]
    public void AllProcessesItem_HasFixedSeriesIndex() {
        var all = new ProcessListItem(string.Empty, "All processes", isAll: true);

        Assert.True(all.IsAll);
        Assert.Equal(1, all.SeriesIndex);
    }
}
