using Beholder.Core;

namespace Beholder.Tests;

public class DestinationSummaryTests {
    [Fact]
    public void Constructor_ValidArguments_PopulatesAllProperties() {
        var summary = new DestinationSummary(
            "93.184.216.34", "example.com", CountryCode.FromAlpha2("US"),
            10_000, 5_000, 3);

        Assert.Equal("93.184.216.34", summary.RemoteAddress);
        Assert.Equal("example.com", summary.Hostname);
        Assert.Equal(CountryCode.FromAlpha2("US"), summary.Country);
        Assert.Equal(10_000, summary.TotalBytesIn);
        Assert.Equal(5_000, summary.TotalBytesOut);
        Assert.Equal(3, summary.ConnectionCount);
    }

    [Fact]
    public void Constructor_NullHostname_Allowed() {
        var summary = new DestinationSummary(
            "1.2.3.4", null, CountryCode.Unknown, 0, 0, 0);
        Assert.Null(summary.Hostname);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_EmptyRemoteAddress_ThrowsArgumentException(string addr) {
        Assert.Throws<ArgumentException>(
            () => new DestinationSummary(addr, null, CountryCode.Unknown, 0, 0, 0));
    }

    [Fact]
    public void Constructor_NullRemoteAddress_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(
            () => new DestinationSummary(null!, null, CountryCode.Unknown, 0, 0, 0));
    }

    [Fact]
    public void Constructor_NegativeBytesIn_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DestinationSummary("1.2.3.4", null, CountryCode.Unknown, -1, 0, 0));
    }

    [Fact]
    public void Constructor_NegativeBytesOut_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DestinationSummary("1.2.3.4", null, CountryCode.Unknown, 0, -1, 0));
    }

    [Fact]
    public void Constructor_NegativeConnectionCount_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DestinationSummary("1.2.3.4", null, CountryCode.Unknown, 0, 0, -1));
    }
}
