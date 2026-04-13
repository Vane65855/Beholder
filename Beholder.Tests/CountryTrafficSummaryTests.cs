using Beholder.Core;

namespace Beholder.Tests;

public class CountryTrafficSummaryTests {
    [Fact]
    public void Constructor_ValidArguments_PopulatesAllProperties() {
        var summary = new CountryTrafficSummary(
            CountryCode.FromAlpha2("DE"), 50_000, 25_000);

        Assert.Equal(CountryCode.FromAlpha2("DE"), summary.Country);
        Assert.Equal(50_000, summary.TotalBytesIn);
        Assert.Equal(25_000, summary.TotalBytesOut);
    }

    [Fact]
    public void Constructor_NegativeBytesIn_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CountryTrafficSummary(CountryCode.Unknown, -1, 0));
    }

    [Fact]
    public void Constructor_NegativeBytesOut_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CountryTrafficSummary(CountryCode.Unknown, 0, -1));
    }

    [Fact]
    public void Constructor_ZeroBytes_Allowed() {
        var summary = new CountryTrafficSummary(CountryCode.Local, 0, 0);
        Assert.Equal(0, summary.TotalBytesIn);
        Assert.Equal(0, summary.TotalBytesOut);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual() {
        var a = new CountryTrafficSummary(CountryCode.FromAlpha2("US"), 100, 200);
        var b = new CountryTrafficSummary(CountryCode.FromAlpha2("US"), 100, 200);
        Assert.Equal(a, b);
    }
}
