using Beholder.Core;

namespace Beholder.Tests;

public class AlertKindTests {
    [Fact]
    public void Unknown_IsZero() {
        Assert.Equal(0, (int)AlertKind.Unknown);
    }

    [Fact]
    public void NonUnknownValues_AreNonzeroAndDistinct() {
        var values = new[] {
            (int)AlertKind.NewProcess,
            (int)AlertKind.HashChanged,
            (int)AlertKind.ChainError,
        };
        Assert.All(values, v => Assert.NotEqual(0, v));
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    [Fact]
    public void AllValues_AreDefined() {
        Assert.True(Enum.IsDefined(AlertKind.Unknown));
        Assert.True(Enum.IsDefined(AlertKind.NewProcess));
        Assert.True(Enum.IsDefined(AlertKind.HashChanged));
        Assert.True(Enum.IsDefined(AlertKind.ChainError));
    }
}
