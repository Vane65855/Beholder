using Beholder.Core;

namespace Beholder.Tests;

public class FirewallActionTests {
    [Fact]
    public void Values_AreDistinctIntegers() {
        Assert.NotEqual((int)FirewallAction.Allow, (int)FirewallAction.Block);
    }

    [Fact]
    public void Values_BothDefined() {
        Assert.True(Enum.IsDefined(FirewallAction.Allow));
        Assert.True(Enum.IsDefined(FirewallAction.Block));
    }
}
