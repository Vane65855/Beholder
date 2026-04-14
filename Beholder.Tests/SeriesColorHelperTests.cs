using Beholder.Ui.Helpers;

namespace Beholder.Tests;

public class SeriesColorHelperTests {
    [Fact]
    public void GetSeriesIndex_NullPath_Throws() =>
        Assert.Throws<ArgumentNullException>(() => SeriesColorHelper.GetSeriesIndex(null!));

    [Fact]
    public void GetSeriesIndex_EmptyPath_Throws() =>
        Assert.Throws<ArgumentException>(() => SeriesColorHelper.GetSeriesIndex(""));

    [Fact]
    public void GetSeriesIndex_WhitespacePath_Throws() =>
        Assert.Throws<ArgumentException>(() => SeriesColorHelper.GetSeriesIndex("   "));

    [Fact]
    public void GetSeriesIndex_ReturnsInRange1To12() {
        var paths = new[] {
            "C:\\Windows\\System32\\svchost.exe",
            "C:\\Program Files\\Firefox\\firefox.exe",
            "/usr/bin/curl",
            "some-random-binary.exe",
        };

        foreach (var path in paths) {
            var index = SeriesColorHelper.GetSeriesIndex(path);
            Assert.InRange(index, 1, 12);
        }
    }

    [Fact]
    public void GetSeriesIndex_Deterministic_SamePathSameResult() {
        var path = "C:\\Windows\\System32\\svchost.exe";
        var first = SeriesColorHelper.GetSeriesIndex(path);
        var second = SeriesColorHelper.GetSeriesIndex(path);
        Assert.Equal(first, second);
    }

    [Fact]
    public void GetSeriesIndex_DifferentPaths_CanProduceDifferentResults() {
        // With enough paths, at least two different indices should appear
        var indices = new HashSet<int>();
        for (var i = 0; i < 100; i++)
            indices.Add(SeriesColorHelper.GetSeriesIndex($"process_{i}.exe"));
        Assert.True(indices.Count > 1);
    }

    [Theory]
    [InlineData(1, "Series01Color")]
    [InlineData(12, "Series12Color")]
    [InlineData(5, "Series05Color")]
    public void GetColorResourceKey_FormatsCorrectly(int index, string expected) =>
        Assert.Equal(expected, SeriesColorHelper.GetColorResourceKey(index));

    [Theory]
    [InlineData(1, "Series01")]
    [InlineData(12, "Series12")]
    [InlineData(5, "Series05")]
    public void GetBrushResourceKey_FormatsCorrectly(int index, string expected) =>
        Assert.Equal(expected, SeriesColorHelper.GetBrushResourceKey(index));
}
