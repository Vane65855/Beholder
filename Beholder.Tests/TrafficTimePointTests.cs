using Beholder.Core;

namespace Beholder.Tests;

public class TrafficTimePointTests {
    [Fact]
    public void Constructor_ValidArguments_PopulatesAllProperties() {
        var ts = new DateTimeOffset(2026, 4, 13, 10, 0, 0, TimeSpan.Zero);
        var point = new TrafficTimePoint(ts, 5000, 3000);

        Assert.Equal(ts, point.Timestamp);
        Assert.Equal(5000, point.BytesIn);
        Assert.Equal(3000, point.BytesOut);
    }

    [Fact]
    public void Constructor_NegativeBytesIn_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TrafficTimePoint(DateTimeOffset.UnixEpoch, -1, 0));
    }

    [Fact]
    public void Constructor_NegativeBytesOut_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TrafficTimePoint(DateTimeOffset.UnixEpoch, 0, -1));
    }

    [Fact]
    public void Constructor_ZeroBytes_Allowed() {
        var point = new TrafficTimePoint(DateTimeOffset.UnixEpoch, 0, 0);
        Assert.Equal(0, point.BytesIn);
        Assert.Equal(0, point.BytesOut);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual() {
        var ts = DateTimeOffset.UnixEpoch;
        var a = new TrafficTimePoint(ts, 100, 200);
        var b = new TrafficTimePoint(ts, 100, 200);
        Assert.Equal(a, b);
    }
}
