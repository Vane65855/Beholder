using System.Text.Json;
using Beholder.Core;
using Beholder.Daemon.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public sealed class SqliteAlertStoreTests : IDisposable {
    private static readonly DateTimeOffset DefaultTimestamp = new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly SqliteAlertStore _store;

    public SqliteAlertStoreTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath).Initialize();
        _store = new SqliteAlertStore(
            new ConnectionFactory(_databasePath),
            NullLogger<SqliteAlertStore>.Instance);
    }

    public void Dispose() {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Ctor_NullConnectionFactory_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() =>
            new SqliteAlertStore(null!, NullLogger<SqliteAlertStore>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() =>
            new SqliteAlertStore(new ConnectionFactory(_databasePath), null!));
    }

    [Fact]
    public async Task GetAlertsAsync_ZeroLimit_ThrowsArgumentOutOfRangeException() {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _store.GetAlertsAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task MarkAlertReadAsync_ZeroSeq_ThrowsArgumentOutOfRangeException() {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _store.MarkAlertReadAsync(0, DefaultTimestamp, CancellationToken.None));
    }

    [Fact]
    public async Task GetAlertsAsync_EmptyDatabase_ReturnsEmptyList() {
        var alerts = await _store.GetAlertsAsync(10, TestContext.Current.CancellationToken);

        Assert.NotNull(alerts);
        Assert.Empty(alerts);
    }

    [Fact]
    public async Task GetAlertsAsync_WithAlerts_ReturnsNewestFirst() {
        await InsertAlertRowAsync(1, "NewProcess", "/bin/a", "First alert", DefaultTimestamp);
        await InsertAlertRowAsync(2, "HashChanged", "/bin/b", "Second alert", DefaultTimestamp.AddMinutes(1));
        await InsertAlertRowAsync(3, "ChainError", "", "Third alert", DefaultTimestamp.AddMinutes(2));

        var alerts = await _store.GetAlertsAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(3, alerts.Count);
        Assert.Equal(3, alerts[0].Seq);
        Assert.Equal(2, alerts[1].Seq);
        Assert.Equal(1, alerts[2].Seq);
    }

    [Fact]
    public async Task GetAlertsAsync_RespectsLimit() {
        for (var i = 1; i <= 5; i++) {
            await InsertAlertRowAsync(i, "NewProcess", $"/bin/p{i}", $"Alert {i}", DefaultTimestamp.AddMinutes(i));
        }

        var alerts = await _store.GetAlertsAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal(2, alerts.Count);
        Assert.Equal(5, alerts[0].Seq);
        Assert.Equal(4, alerts[1].Seq);
    }

    [Fact]
    public async Task GetAlertsAsync_IgnoresNonAlertEvents() {
        await InsertAlertRowAsync(1, "Counter", "/bin/a", "Counter event", DefaultTimestamp);
        await InsertAlertRowAsync(2, "NewProcess", "/bin/b", "Real alert", DefaultTimestamp.AddMinutes(1));
        await InsertAlertRowAsync(3, "FirewallRuleCreated", "/bin/c", "Rule event", DefaultTimestamp.AddMinutes(2));

        var alerts = await _store.GetAlertsAsync(10, TestContext.Current.CancellationToken);

        var only = Assert.Single(alerts);
        Assert.Equal(2, only.Seq);
        Assert.Equal("Real alert", only.Summary);
    }

    [Fact]
    public async Task GetAlertsAsync_UnreadAlerts_HaveNullFirstViewedAt() {
        await InsertAlertRowAsync(1, "NewProcess", "/bin/a", "An alert", DefaultTimestamp);

        var alerts = await _store.GetAlertsAsync(10, TestContext.Current.CancellationToken);

        var alert = Assert.Single(alerts);
        Assert.Null(alert.FirstViewedAt);
        Assert.False(alert.IsRead);
    }

    [Fact]
    public async Task GetAlertsAsync_PreservesPayloadFields() {
        await InsertAlertRowAsync(1, "NewProcess", @"C:\Windows\System32\curl.exe", "New process detected", DefaultTimestamp);

        var alerts = await _store.GetAlertsAsync(10, TestContext.Current.CancellationToken);

        var alert = Assert.Single(alerts);
        Assert.Equal(@"C:\Windows\System32\curl.exe", alert.ProcessPath);
        Assert.Equal("New process detected", alert.Summary);
        Assert.Equal(AlertKind.NewProcess, alert.Kind);
    }

    [Fact]
    public async Task GetAlertsAsync_MapsEventKindStringsToAlertKindEnum() {
        await InsertAlertRowAsync(1, "NewProcess", "/bin/a", "NP alert", DefaultTimestamp);
        await InsertAlertRowAsync(2, "HashChanged", "/bin/b", "HC alert", DefaultTimestamp.AddMinutes(1));
        await InsertAlertRowAsync(3, "ChainError", "", "CE alert", DefaultTimestamp.AddMinutes(2));

        var alerts = await _store.GetAlertsAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(AlertKind.ChainError, alerts[0].Kind);
        Assert.Equal(AlertKind.HashChanged, alerts[1].Kind);
        Assert.Equal(AlertKind.NewProcess, alerts[2].Kind);
    }

    [Fact]
    public async Task MarkAlertReadAsync_SetsFirstViewedAt() {
        await InsertAlertRowAsync(1, "NewProcess", "/bin/a", "An alert", DefaultTimestamp);
        var viewedAt = DefaultTimestamp.AddHours(1);

        await _store.MarkAlertReadAsync(1, viewedAt, TestContext.Current.CancellationToken);

        var alerts = await _store.GetAlertsAsync(10, TestContext.Current.CancellationToken);
        var alert = Assert.Single(alerts);
        Assert.NotNull(alert.FirstViewedAt);
        Assert.Equal(viewedAt, alert.FirstViewedAt);
        Assert.True(alert.IsRead);
    }

    [Fact]
    public async Task MarkAlertReadAsync_Idempotent_PreservesOriginalTimestamp() {
        await InsertAlertRowAsync(1, "NewProcess", "/bin/a", "An alert", DefaultTimestamp);
        var firstViewedAt = DefaultTimestamp.AddHours(1);
        var secondViewedAt = DefaultTimestamp.AddHours(5);

        await _store.MarkAlertReadAsync(1, firstViewedAt, TestContext.Current.CancellationToken);
        await _store.MarkAlertReadAsync(1, secondViewedAt, TestContext.Current.CancellationToken);

        var alerts = await _store.GetAlertsAsync(10, TestContext.Current.CancellationToken);
        var alert = Assert.Single(alerts);
        Assert.Equal(firstViewedAt, alert.FirstViewedAt);
    }

    [Fact]
    public async Task GetAlertsAsync_MalformedPayload_ReturnsPlaceholderSummary() {
        using var connection = new ConnectionFactory(_databasePath).CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO event_log (seq, ts_unix_ns, kind, payload, prev_hash, row_hash)
            VALUES ($seq, $ts, $kind, $payload, $zeros, $zeros);
            """;
        command.Parameters.AddWithValue("$seq", 1);
        command.Parameters.AddWithValue("$ts", DefaultTimestamp.ToUnixTimeMilliseconds() * 1_000_000L);
        command.Parameters.AddWithValue("$kind", "NewProcess");
        command.Parameters.AddWithValue("$payload", new byte[] { 0xFF, 0xFF });
        command.Parameters.AddWithValue("$zeros", new byte[32]);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        var alerts = await _store.GetAlertsAsync(10, TestContext.Current.CancellationToken);

        var alert = Assert.Single(alerts);
        Assert.Equal("(malformed payload)", alert.Summary);
        Assert.Equal("", alert.ProcessPath);
    }

    private async Task InsertAlertRowAsync(
        long seq,
        string eventKind,
        string processPath,
        string summary,
        DateTimeOffset timestamp
    ) {
        using var connection = new ConnectionFactory(_databasePath).CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO event_log (seq, ts_unix_ns, kind, payload, prev_hash, row_hash)
            VALUES ($seq, $ts, $kind, $payload, $zeros, $zeros);
            """;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new {
            processPath,
            summary,
        });
        command.Parameters.AddWithValue("$seq", seq);
        command.Parameters.AddWithValue("$ts", timestamp.ToUnixTimeMilliseconds() * 1_000_000L);
        command.Parameters.AddWithValue("$kind", eventKind);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$zeros", new byte[32]);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
