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

    [Fact]
    public void Ctor_NullProcessPath_Throws() =>
        Assert.Throws<ArgumentNullException>("processPath",
            () => new ProcessListItem(null!, "test.exe"));

    [Fact]
    public void Ctor_EmptyProcessPath_WithoutIsAll_Throws() =>
        Assert.Throws<ArgumentException>("processPath",
            () => new ProcessListItem(string.Empty, "test.exe", isAll: false));

    [Fact]
    public void Ctor_EmptyProcessPath_WithIsAll_DoesNotThrow() {
        // The "All processes" aggregate row legitimately uses an empty path.
        var item = new ProcessListItem(string.Empty, "All processes", isAll: true);

        Assert.True(item.IsAll);
        Assert.Equal(string.Empty, item.ProcessPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_NullOrWhitespaceDisplayName_Throws(string? displayName) {
        // ThrowIfNullOrWhiteSpace throws ArgumentNullException for null and
        // ArgumentException for empty/whitespace — both derive from
        // ArgumentException, so ThrowsAny handles both cases.
        var ex = Assert.ThrowsAny<ArgumentException>(
            () => new ProcessListItem("fake/test.exe", displayName!));
        Assert.Equal("displayName", ex.ParamName);
    }
}
