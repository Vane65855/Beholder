using Beholder.Daemon.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public sealed class SqliteSettingsOverridesStoreTests : IDisposable {
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly FakeTimeProvider _timeProvider;
    private readonly SqliteSettingsOverridesStore _store;

    public SqliteSettingsOverridesStoreTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();
        _timeProvider = new FakeTimeProvider(BaseTime);
        _store = new SqliteSettingsOverridesStore(
            new ConnectionFactory(_databasePath, pooling: false), _timeProvider);
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task GetAsync_UnknownKey_ReturnsNull() {
        var result = await _store.GetAsync("Nonexistent.Key", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertAsync_Then_GetAsync_RoundTrips() {
        await _store.UpsertAsync("Recording.FilterSelfTraffic", "true", CancellationToken.None);

        var result = await _store.GetAsync("Recording.FilterSelfTraffic", CancellationToken.None);

        Assert.Equal("true", result);
    }

    [Fact]
    public async Task UpsertAsync_ExistingKey_OverwritesValueAndUpdatesTimestamp() {
        await _store.UpsertAsync("Dns.EnablePreload", "true", CancellationToken.None);
        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        await _store.UpsertAsync("Dns.EnablePreload", "false", CancellationToken.None);

        var result = await _store.GetAsync("Dns.EnablePreload", CancellationToken.None);

        Assert.Equal("false", result);
    }

    [Fact]
    public async Task ListAllAsync_ReturnsEveryRow() {
        await _store.UpsertAsync("Recording.FilterSelfTraffic", "false", CancellationToken.None);
        await _store.UpsertAsync("Dns.EnablePreload", "true", CancellationToken.None);
        await _store.UpsertAsync("Sni.EnableSniCapture", "false", CancellationToken.None);

        var all = await _store.ListAllAsync(CancellationToken.None);

        Assert.Equal(3, all.Count);
        Assert.Equal("false", all["Recording.FilterSelfTraffic"]);
        Assert.Equal("true", all["Dns.EnablePreload"]);
        Assert.Equal("false", all["Sni.EnableSniCapture"]);
    }

    [Fact]
    public async Task GetAsync_EmptyKey_ThrowsArgumentException() {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.GetAsync("", CancellationToken.None));
    }
}
