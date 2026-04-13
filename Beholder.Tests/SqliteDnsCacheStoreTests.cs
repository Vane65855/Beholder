using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public class SqliteDnsCacheStoreTests : IDisposable {
    private readonly string _tempDir;
    private readonly SqliteDnsCacheStore _store;

    public SqliteDnsCacheStoreTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        var databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(databasePath, pooling: false).Initialize();
        var connectionFactory = new ConnectionFactory(databasePath, pooling: false);
        _store = new SqliteDnsCacheStore(connectionFactory);
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task UpsertBatchAsync_EmptyList_DoesNotThrow() {
        await _store.UpsertBatchAsync([], CancellationToken.None);
    }

    [Fact]
    public async Task UpsertBatchAsync_SingleEntry_CanBeResolved() {
        await _store.UpsertBatchAsync(
            [("93.184.216.34", "example.com")], CancellationToken.None);

        var hostname = await _store.ResolveAsync("93.184.216.34", CancellationToken.None);
        Assert.Equal("example.com", hostname);
    }

    [Fact]
    public async Task UpsertBatchAsync_MultipleEntries_AllResolvable() {
        var entries = new List<(string, string)> {
            ("1.1.1.1", "one.one.one.one"),
            ("8.8.8.8", "dns.google"),
            ("9.9.9.9", "dns.quad9.net")
        };
        await _store.UpsertBatchAsync(entries, CancellationToken.None);

        Assert.Equal("one.one.one.one", await _store.ResolveAsync("1.1.1.1", CancellationToken.None));
        Assert.Equal("dns.google", await _store.ResolveAsync("8.8.8.8", CancellationToken.None));
        Assert.Equal("dns.quad9.net", await _store.ResolveAsync("9.9.9.9", CancellationToken.None));
    }

    [Fact]
    public async Task UpsertBatchAsync_DuplicateAddress_UpdatesHostname() {
        await _store.UpsertBatchAsync(
            [("1.1.1.1", "old-hostname.com")], CancellationToken.None);
        await _store.UpsertBatchAsync(
            [("1.1.1.1", "new-hostname.com")], CancellationToken.None);

        var hostname = await _store.ResolveAsync("1.1.1.1", CancellationToken.None);
        Assert.Equal("new-hostname.com", hostname);
    }

    [Fact]
    public async Task ResolveAsync_UnknownAddress_ReturnsNull() {
        var hostname = await _store.ResolveAsync("10.0.0.1", CancellationToken.None);
        Assert.Null(hostname);
    }

    [Fact]
    public async Task PruneAsync_DeletesStaleEntries_ReturnsCount() {
        // Insert an entry, then prune with a cutoff in the future
        await _store.UpsertBatchAsync(
            [("1.1.1.1", "example.com")], CancellationToken.None);

        var deleted = await _store.PruneAsync(
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Null(await _store.ResolveAsync("1.1.1.1", CancellationToken.None));
    }

    [Fact]
    public async Task PruneAsync_KeepsFreshEntries() {
        await _store.UpsertBatchAsync(
            [("1.1.1.1", "example.com")], CancellationToken.None);

        var deleted = await _store.PruneAsync(
            DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.Equal("example.com", await _store.ResolveAsync("1.1.1.1", CancellationToken.None));
    }

    [Fact]
    public async Task PruneAsync_NothingToDelete_ReturnsZero() {
        var deleted = await _store.PruneAsync(
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(0, deleted);
    }
}
