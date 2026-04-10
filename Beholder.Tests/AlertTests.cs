using Beholder.Core;

namespace Beholder.Tests;

public class AlertTests {
    [Fact]
    public void Constructor_FirstViewedAtNull_IsReadFalse() {
        var alert = new Alert(
            seq: 1,
            kind: AlertKind.NewProcess,
            processPath: "/usr/bin/curl",
            summary: "curl first network activity",
            timestamp: DateTimeOffset.UnixEpoch,
            firstViewedAt: null
        );

        Assert.False(alert.IsRead);
    }

    [Fact]
    public void Constructor_FirstViewedAtSet_IsReadTrue() {
        var viewed = new DateTimeOffset(2026, 4, 10, 9, 30, 0, TimeSpan.Zero);

        var alert = new Alert(
            seq: 1,
            kind: AlertKind.NewProcess,
            processPath: "/usr/bin/curl",
            summary: "curl first network activity",
            timestamp: DateTimeOffset.UnixEpoch,
            firstViewedAt: viewed
        );

        Assert.True(alert.IsRead);
    }

    [Fact]
    public void Constructor_NullSummary_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => new Alert(
            seq: 1,
            kind: AlertKind.NewProcess,
            processPath: "/usr/bin/curl",
            summary: null!,
            timestamp: DateTimeOffset.UnixEpoch,
            firstViewedAt: null
        ));
    }

    [Fact]
    public void Constructor_EmptyProcessPath_AllowedForChainError() {
        var alert = new Alert(
            seq: 99,
            kind: AlertKind.ChainError,
            processPath: "",
            summary: "chain verification failed at seq 42",
            timestamp: DateTimeOffset.UnixEpoch,
            firstViewedAt: null
        );

        Assert.Equal("", alert.ProcessPath);
        Assert.Equal(AlertKind.ChainError, alert.Kind);
    }
}
