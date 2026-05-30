using Beholder.Core;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public sealed class SqliteCheckpointStoreTests : IDisposable {
    private static readonly DateTimeOffset FixedTime = new(2026, 5, 28, 14, 30, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly ConnectionFactory _connectionFactory;
    private readonly SqliteCheckpointStore _store;

    public SqliteCheckpointStoreTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();
        _connectionFactory = new ConnectionFactory(_databasePath, pooling: false);
        _store = new SqliteCheckpointStore(_connectionFactory);
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Constructor_NullConnectionFactory_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => new SqliteCheckpointStore(null!));
    }

    [Fact]
    public async Task GetLatestAsync_EmptyTable_ReturnsNull() {
        var result = await _store.GetLatestAsync(CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task AppendAsync_ThenGetLatest_RoundTripsAllFields() {
        var rowHash = new byte[ChainHasher.HashSize];
        Array.Fill(rowHash, (byte)0xA5);
        var signature = new byte[64];
        Array.Fill(signature, (byte)0xC3);
        var checkpoint = new Checkpoint(
            Seq: 42L,
            RowHash: rowHash,
            Timestamp: FixedTime,
            Signature: signature,
            KeyId: "abc123def4567890");

        await _store.AppendAsync(checkpoint, CancellationToken.None);
        var latest = await _store.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(42L, latest.Seq);
        Assert.Equal(rowHash, latest.RowHash);
        Assert.Equal(FixedTime.ToUnixTimeMilliseconds(), latest.Timestamp.ToUnixTimeMilliseconds());
        Assert.Equal(signature, latest.Signature);
        Assert.Equal("abc123def4567890", latest.KeyId);
    }

    [Fact]
    public async Task AppendAsync_MultipleCheckpoints_GetLatestReturnsHighestSeq() {
        var rowHash = new byte[ChainHasher.HashSize];
        var signature = new byte[64];
        await _store.AppendAsync(
            new Checkpoint(10L, rowHash, FixedTime, signature, "k1"),
            CancellationToken.None);
        await _store.AppendAsync(
            new Checkpoint(20L, rowHash, FixedTime, signature, "k2"),
            CancellationToken.None);
        await _store.AppendAsync(
            new Checkpoint(15L, rowHash, FixedTime, signature, "k3"),
            CancellationToken.None);

        var latest = await _store.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(20L, latest.Seq);
        Assert.Equal("k2", latest.KeyId);
    }

    [Fact]
    public async Task AppendAsync_DuplicateSeq_ThrowsSqliteException() {
        var rowHash = new byte[ChainHasher.HashSize];
        var signature = new byte[64];
        await _store.AppendAsync(
            new Checkpoint(7L, rowHash, FixedTime, signature, "k1"),
            CancellationToken.None);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            _store.AppendAsync(
                new Checkpoint(7L, rowHash, FixedTime, signature, "k2"),
                CancellationToken.None));
    }

    [Fact]
    public async Task AppendAsync_NullCheckpoint_ThrowsArgumentNullException() {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _store.AppendAsync(null!, CancellationToken.None));
    }
}
