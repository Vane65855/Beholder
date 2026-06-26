using Beholder.Core;

namespace Beholder.Tests;

public class ProcessSentinelsTests {
    [Fact]
    public void Constants_AreTheResolverSentinelStrings() {
        Assert.Equal("System", ProcessSentinels.System);
        Assert.Equal("unknown", ProcessSentinels.Unknown);
    }

    [Theory]
    [InlineData("unknown", true)]
    [InlineData("System", false)]
    [InlineData(@"C:\bin\app.exe", false)]
    [InlineData("Unknown", false)]   // case-sensitive: only the exact sentinel
    public void IsUnknown_MatchesOnlyTheUnknownSentinel(string path, bool expected) {
        Assert.Equal(expected, ProcessSentinels.IsUnknown(path));
    }

    [Theory]
    [InlineData("unknown", true)]
    [InlineData("System", true)]
    [InlineData(@"C:\bin\app.exe", false)]
    [InlineData("system", false)]    // case-sensitive
    public void IsNonTargetable_MatchesEitherSentinel(string path, bool expected) {
        Assert.Equal(expected, ProcessSentinels.IsNonTargetable(path));
    }
}
