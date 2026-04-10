using Beholder.Core;

namespace Beholder.Tests;

public class DirectionTests {
    [Fact]
    public void Values_AreDistinctIntegers() {
        Assert.NotEqual((int)Direction.Inbound, (int)Direction.Outbound);
    }

    [Fact]
    public void Values_BothDefined() {
        Assert.True(Enum.IsDefined(Direction.Inbound));
        Assert.True(Enum.IsDefined(Direction.Outbound));
    }
}
