using System.Text.Json;
using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Grpc;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Beholder.Tests.TestDoubles;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Local = Beholder.Protocol.Local;

namespace Beholder.Tests;

public sealed class MarkAlertReadTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly ConnectionFactory _connectionFactory;
    private readonly SqliteAlertStore _alertStore;
    private readonly FakeTimeProvider _timeProvider;
    private readonly BroadcastService _broadcaster;
    private readonly BeholderLocalService _service;

    public MarkAlertReadTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();

        _connectionFactory = new ConnectionFactory(_databasePath, pooling: false);
        _alertStore = new SqliteAlertStore(_connectionFactory, NullLogger<SqliteAlertStore>.Instance);
        _timeProvider = new FakeTimeProvider(FixedTimestamp);
        var eventStore = new SqliteEventStore(_connectionFactory, _timeProvider);
        var firewallStore = new SqliteFirewallRuleStore(_connectionFactory);
        var snapshotSource = new FakeSnapshotBatchSource();
        _broadcaster = new BroadcastService(
            snapshotSource, _timeProvider, NullLogger<BroadcastService>.Instance);
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), _timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeRecordingSettingsState(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);

        _service = new BeholderLocalService(
            _broadcaster, pipeline, firewallStore, _alertStore,
            new FakeFirewallController(), new FakeFirewallEnforcementState(),
            eventStore, new FakeTrafficStore(),
            new FakeLanDeviceStore(), TestServiceFactory.CreateInactiveLanScannerService(),
            new FakeChainStatusCache(), new FakeChainVerifier(), new FakeStorageStatsProvider(),
            new FakeRecordingSettingsState(), new FakeHostnameResolutionSettingsState(),
            new FakeAlertSettingsState(),
            new FakeScannerSettingsState(),
            new FakeSettingsOverridesStore(),
            new FakeAppIdentityRuleStore(),
            _timeProvider, NullLogger<BeholderLocalService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task MarkAlertRead_ValidSeq_MarksRead() {
        await InsertAlertRowAsync(1, "NewProcess", "/bin/a", "Test alert", FixedTimestamp);
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        await _service.MarkAlertRead(new Local.MarkAlertReadRequest { Seq = 1 }, context);

        var alerts = await _alertStore.GetAlertsAsync(10, TestContext.Current.CancellationToken);
        var alert = Assert.Single(alerts);
        Assert.NotNull(alert.FirstViewedAt);
        Assert.True(alert.IsRead);
    }

    [Fact]
    public async Task MarkAlertRead_ZeroSeq_ReturnsInvalidArgument() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _service.MarkAlertRead(new Local.MarkAlertReadRequest { Seq = 0 }, context));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task MarkAlertRead_NegativeSeq_ReturnsInvalidArgument() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _service.MarkAlertRead(new Local.MarkAlertReadRequest { Seq = -1 }, context));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task MarkAlertRead_NonexistentSeq_ReturnsSuccess() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.MarkAlertRead(
            new Local.MarkAlertReadRequest { Seq = 999 }, context);

        Assert.NotNull(response);
    }

    [Fact]
    public async Task MarkAlertRead_Idempotent_PreservesFirstViewedAt() {
        await InsertAlertRowAsync(1, "NewProcess", "/bin/a", "Test alert", FixedTimestamp);
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        await _service.MarkAlertRead(new Local.MarkAlertReadRequest { Seq = 1 }, context);
        _timeProvider.Advance(TimeSpan.FromHours(5));
        await _service.MarkAlertRead(new Local.MarkAlertReadRequest { Seq = 1 }, context);

        var alerts = await _alertStore.GetAlertsAsync(10, TestContext.Current.CancellationToken);
        var alert = Assert.Single(alerts);
        Assert.Equal(FixedTimestamp, alert.FirstViewedAt);
    }

    private async Task InsertAlertRowAsync(
        long seq, string eventKind, string processPath,
        string summary, DateTimeOffset timestamp
    ) {
        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO event_log (seq, ts_unix_ns, kind, payload, prev_hash, row_hash)
            VALUES ($seq, $ts, $kind, $payload, $zeros, $zeros);
            """;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { processPath, summary });
        command.Parameters.AddWithValue("$seq", seq);
        command.Parameters.AddWithValue("$ts", timestamp.ToUnixTimeMilliseconds() * 1_000_000L);
        command.Parameters.AddWithValue("$kind", eventKind);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$zeros", new byte[32]);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

}
