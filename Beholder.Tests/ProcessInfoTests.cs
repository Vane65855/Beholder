using Beholder.Core;

namespace Beholder.Tests;

public class ProcessInfoTests {
    [Fact]
    public void Constructor_ValidArguments_PopulatesAllProperties() {
        var firstSeen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lastSeen = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero);
        var lastHashed = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);
        var sha = new byte[32];

        var info = new ProcessInfo(
            path: "/usr/bin/curl",
            displayName: "curl",
            sha256: sha,
            firstSeen: firstSeen,
            lastSeen: lastSeen,
            lastHashedAt: lastHashed
        );

        Assert.Equal("/usr/bin/curl", info.Path);
        Assert.Equal("curl", info.DisplayName);
        Assert.Same(sha, info.Sha256);
        Assert.Equal(firstSeen, info.FirstSeen);
        Assert.Equal(lastSeen, info.LastSeen);
        Assert.Equal(lastHashed, info.LastHashedAt);
    }

    [Fact]
    public void Constructor_NullPath_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => new ProcessInfo(
            path: null!,
            displayName: "curl",
            sha256: null,
            firstSeen: DateTimeOffset.UnixEpoch,
            lastSeen: DateTimeOffset.UnixEpoch,
            lastHashedAt: null
        ));
    }

    [Fact]
    public void Constructor_NullSha256_Allowed() {
        var info = new ProcessInfo(
            path: "/usr/bin/curl",
            displayName: "curl",
            sha256: null,
            firstSeen: DateTimeOffset.UnixEpoch,
            lastSeen: DateTimeOffset.UnixEpoch,
            lastHashedAt: null
        );

        Assert.Null(info.Sha256);
        Assert.Null(info.LastHashedAt);
    }
}
