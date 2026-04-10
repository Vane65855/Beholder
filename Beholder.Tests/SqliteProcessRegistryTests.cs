using Beholder.Core;
using Beholder.Daemon.Storage;
using Microsoft.Data.Sqlite;

namespace Beholder.Tests;

public sealed class SqliteProcessRegistryTests : IDisposable {
    private static readonly DateTimeOffset DefaultTimestamp = new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly SqliteProcessRegistry _registry;

    public SqliteProcessRegistryTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath).Initialize();
        _registry = new SqliteProcessRegistry(new ConnectionFactory(_databasePath));
    }

    public void Dispose() {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task RegisterAsync_NewProcess_Inserts() {
        var info = MakeProcessInfo();

        await _registry.RegisterAsync(info, CancellationToken.None);

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(info.Path, fetched.Path);
        Assert.Equal(info.DisplayName, fetched.DisplayName);
        Assert.Equal(info.Sha256, fetched.Sha256);
        Assert.Equal(info.FirstSeen, fetched.FirstSeen);
        Assert.Equal(info.LastSeen, fetched.LastSeen);
        Assert.Equal(info.LastHashedAt, fetched.LastHashedAt);
    }

    [Fact]
    public async Task RegisterAsync_ExistingProcess_UpdatesLastSeenAndSha256() {
        var firstTime = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var laterTime = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero);
        var newHash = new byte[32];
        Array.Fill(newHash, (byte)0x01);

        await _registry.RegisterAsync(
            MakeProcessInfo(sha256: null, lastSeen: firstTime),
            CancellationToken.None
        );
        await _registry.RegisterAsync(
            MakeProcessInfo(sha256: newHash, lastSeen: laterTime),
            CancellationToken.None
        );

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(newHash, fetched.Sha256);
        Assert.Equal(laterTime, fetched.LastSeen);
    }

    [Fact]
    public async Task RegisterAsync_PreservesFirstSeen_OnUpdate() {
        var originalFirstSeen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var attemptedNewFirstSeen = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await _registry.RegisterAsync(
            MakeProcessInfo(firstSeen: originalFirstSeen),
            CancellationToken.None
        );
        await _registry.RegisterAsync(
            MakeProcessInfo(firstSeen: attemptedNewFirstSeen),
            CancellationToken.None
        );

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(originalFirstSeen, fetched.FirstSeen);
    }

    [Fact]
    public async Task GetByPathAsync_NotFound_ReturnsNull() {
        var fetched = await _registry.GetByPathAsync("/never/registered", CancellationToken.None);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task GetByPathAsync_WithSha256_RoundTrips() {
        var hash = new byte[32];
        Array.Fill(hash, (byte)0xAB);

        await _registry.RegisterAsync(MakeProcessInfo(sha256: hash), CancellationToken.None);

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.NotNull(fetched.Sha256);
        Assert.True(hash.AsSpan().SequenceEqual(fetched.Sha256));
    }

    [Fact]
    public async Task GetByPathAsync_WithNullSha256_RoundTrips() {
        await _registry.RegisterAsync(MakeProcessInfo(sha256: null), CancellationToken.None);

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Null(fetched.Sha256);
    }

    [Fact]
    public async Task GetByPathAsync_WithLastHashedAt_RoundTrips() {
        var hashedAt = new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);

        await _registry.RegisterAsync(MakeProcessInfo(lastHashedAt: hashedAt), CancellationToken.None);

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(hashedAt, fetched.LastHashedAt);
    }

    [Fact]
    public async Task GetByPathAsync_WithNullLastHashedAt_RoundTrips() {
        await _registry.RegisterAsync(MakeProcessInfo(lastHashedAt: null), CancellationToken.None);

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Null(fetched.LastHashedAt);
    }

    [Fact]
    public async Task ListAllAsync_MultipleProcesses_ReturnsAllOrderedByLastSeen() {
        var t1 = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 4, 5, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero);

        await _registry.RegisterAsync(MakeProcessInfo(path: "/usr/bin/curl", lastSeen: t1), CancellationToken.None);
        await _registry.RegisterAsync(MakeProcessInfo(path: "/usr/bin/wget", lastSeen: t2), CancellationToken.None);
        await _registry.RegisterAsync(MakeProcessInfo(path: "/usr/bin/ssh", lastSeen: t3), CancellationToken.None);

        var all = await _registry.ListAllAsync(CancellationToken.None);

        Assert.Equal(3, all.Count);
        Assert.Equal("/usr/bin/ssh", all[0].Path);
        Assert.Equal("/usr/bin/wget", all[1].Path);
        Assert.Equal("/usr/bin/curl", all[2].Path);
    }

    [Fact]
    public async Task ListAllAsync_EmptyTable_ReturnsEmptyList() {
        var all = await _registry.ListAllAsync(CancellationToken.None);

        Assert.NotNull(all);
        Assert.Empty(all);
    }

    [Fact]
    public async Task RegisterAsync_DefensiveCopySha256_MutatingOriginalDoesNotAffectStored() {
        var original = new byte[32];
        Array.Fill(original, (byte)0x11);

        await _registry.RegisterAsync(MakeProcessInfo(sha256: original), CancellationToken.None);
        original[0] = 0xFF;

        var fetched = await _registry.GetByPathAsync("/usr/bin/curl", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.NotNull(fetched.Sha256);
        Assert.Equal((byte)0x11, fetched.Sha256[0]);
    }

    private static ProcessInfo MakeProcessInfo(
        string path = "/usr/bin/curl",
        string displayName = "curl",
        byte[]? sha256 = null,
        DateTimeOffset? firstSeen = null,
        DateTimeOffset? lastSeen = null,
        DateTimeOffset? lastHashedAt = null
    ) => new ProcessInfo(
        path: path,
        displayName: displayName,
        sha256: sha256,
        firstSeen: firstSeen ?? DefaultTimestamp,
        lastSeen: lastSeen ?? DefaultTimestamp,
        lastHashedAt: lastHashedAt
    );
}
