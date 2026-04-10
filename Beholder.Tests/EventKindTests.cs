using Beholder.Core;

namespace Beholder.Tests;

public class EventKindTests {
    [Fact]
    public void Unknown_IsZero() {
        Assert.Equal(0, (int)EventKind.Unknown);
    }

    [Fact]
    public void NonUnknownValues_AreNonzeroAndDistinct() {
        var values = new[] {
            (int)EventKind.Counter,
            (int)EventKind.NewProcess,
            (int)EventKind.HashChanged,
            (int)EventKind.ChainError,
            (int)EventKind.FirewallRuleCreated,
            (int)EventKind.FirewallRuleChanged,
            (int)EventKind.FirewallRuleRemoved,
        };
        Assert.All(values, v => Assert.NotEqual(0, v));
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    [Fact]
    public void AllValues_AreDefined() {
        Assert.True(Enum.IsDefined(EventKind.Unknown));
        Assert.True(Enum.IsDefined(EventKind.Counter));
        Assert.True(Enum.IsDefined(EventKind.NewProcess));
        Assert.True(Enum.IsDefined(EventKind.HashChanged));
        Assert.True(Enum.IsDefined(EventKind.ChainError));
        Assert.True(Enum.IsDefined(EventKind.FirewallRuleCreated));
        Assert.True(Enum.IsDefined(EventKind.FirewallRuleChanged));
        Assert.True(Enum.IsDefined(EventKind.FirewallRuleRemoved));
    }
}
