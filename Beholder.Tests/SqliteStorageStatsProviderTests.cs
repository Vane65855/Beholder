using Beholder.Core;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public sealed class SqliteStorageStatsProviderTests : IDisposable {
    private static readonly DateTimeOffset DaemonStartTime =
        new(2026, 5, 22, 10, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath;
    private readonly ConnectionFactory _connectionFactory;
    private readonly FakeChainStatusCache _chainStatusCache;
    private readonly FakeDaemonClock _daemonClock;

    public SqliteStorageStatsProviderTests() {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"beholder-storage-stats-{Guid.NewGuid():N}.db");
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();
        _connectionFactory = new ConnectionFactory(_databasePath, pooling: false);
        _chainStatusCache = new FakeChainStatusCache();
        _daemonClock = new FakeDaemonClock(DaemonStartTime);
    }

    public void Dispose() {
        // Connection-pool cleanup so the file unlocks; harmless if pool is empty.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }

    [Fact]
    public async Task GetAsync_EmptyDatabase_ReturnsZeroRowsPerTable() {
        var provider = CreateProvider();
        var stats = await provider.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(_databasePath, stats.DatabasePath);
        Assert.True(stats.DatabaseBytesTotal > 0,
            "DB file should have schema overhead even when all user tables are empty.");
        Assert.NotEmpty(stats.Tables);
        Assert.All(stats.Tables, t => Assert.Equal(0, t.RowCount));
    }

    [Fact]
    public async Task GetAsync_ListsKnownTables_ExcludesSqliteInternal() {
        var provider = CreateProvider();
        var stats = await provider.GetAsync(TestContext.Current.CancellationToken);

        var names = stats.Tables.Select(t => t.Name).ToHashSet();
        // Spot-check tables we know the daemon creates per DatabaseInitializer.
        Assert.Contains("traffic_raw", names);
        Assert.Contains("traffic_buckets_10s", names);
        Assert.Contains("event_log", names);
        Assert.Contains("firewall_rules", names);
        Assert.Contains("lan_device", names);

        // sqlite_master / sqlite_sequence / etc. are SQLite internals — must
        // not appear in the user-facing list.
        Assert.DoesNotContain(names, n => n.StartsWith("sqlite_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAsync_ChainStatusNull_WhenCacheEmpty() {
        var provider = CreateProvider();
        var stats = await provider.GetAsync(TestContext.Current.CancellationToken);
        Assert.Null(stats.ChainStatus);
    }

    [Fact]
    public async Task GetAsync_ChainStatusForwarded_WhenCachePopulated() {
        var verifiedAt = new DateTimeOffset(2026, 5, 22, 14, 30, 0, TimeSpan.Zero);
        _chainStatusCache.Update(ChainVerificationResult.Success(rowsVerified: 42), verifiedAt);

        var provider = CreateProvider();
        var stats = await provider.GetAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(stats.ChainStatus);
        Assert.Equal(verifiedAt, stats.ChainStatus!.LastVerifiedAt);
        Assert.True(stats.ChainStatus.Result.IsValid);
        Assert.Equal(42, stats.ChainStatus.Result.RowsVerified);
    }

    [Fact]
    public async Task GetAsync_DatabaseSizeMatchesFileInfo() {
        var provider = CreateProvider();
        var stats = await provider.GetAsync(TestContext.Current.CancellationToken);
        var fileInfo = new FileInfo(_databasePath);
        Assert.Equal(fileInfo.Length, stats.DatabaseBytesTotal);
    }

    [Fact]
    public async Task GetAsync_DaemonStartedAtForwarded_FromInjectedClock() {
        var provider = CreateProvider();
        var stats = await provider.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DaemonStartTime, stats.DaemonStartedAt);
    }

    [Fact]
    public async Task GetAsync_ChainFirstEventAtNull_WhenEventLogEmpty() {
        var provider = CreateProvider();
        var stats = await provider.GetAsync(TestContext.Current.CancellationToken);
        Assert.Null(stats.ChainFirstEventAt);
    }

    [Fact]
    public async Task GetAsync_LanDeviceCountZero_WhenTableEmpty() {
        var provider = CreateProvider();
        var stats = await provider.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, stats.LanDeviceCount);
    }

    private SqliteStorageStatsProvider CreateProvider() => new(
        _connectionFactory, _chainStatusCache, _daemonClock, _databasePath);
}
