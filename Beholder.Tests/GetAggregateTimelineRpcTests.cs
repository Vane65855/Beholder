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

public sealed class GetAggregateTimelineRpcTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly BroadcastService _broadcaster;
    private readonly BeholderLocalService _service;

    public GetAggregateTimelineRpcTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        var databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(databasePath, pooling: false).Initialize();

        var connectionFactory = new ConnectionFactory(databasePath, pooling: false);
        var timeProvider = new FakeTimeProvider(FixedTimestamp);
        var eventStore = new SqliteEventStore(connectionFactory, timeProvider);
        var firewallStore = new SqliteFirewallRuleStore(connectionFactory);
        var alertStore = new SqliteAlertStore(connectionFactory, NullLogger<SqliteAlertStore>.Instance);
        var trafficStore = new SqliteTrafficStore(
            connectionFactory,
            new FakeOptionsMonitor<RollupOptions>(new RollupOptions()),
            timeProvider);
        _broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(), timeProvider, NullLogger<BroadcastService>.Instance);
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeRecordingSettingsState(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);

        _service = new BeholderLocalService(
            _broadcaster, pipeline, firewallStore, alertStore,
            new FakeFirewallController(), new FakeFirewallEnforcementState(),
            eventStore, trafficStore,
            new FakeLanDeviceStore(), TestServiceFactory.CreateInactiveLanScannerService(),
            new FakeChainStatusCache(), new FakeChainVerifier(), new FakeStorageStatsProvider(),
            new FakeRecordingSettingsState(), new FakeHostnameResolutionSettingsState(),
            new FakeAlertSettingsState(),
            new FakeScannerSettingsState(),
            new FakeSettingsOverridesStore(),
            new FakeAppIdentityRuleStore(),
            timeProvider, NullLogger<BeholderLocalService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task GetAggregateTimeline_InvertedRange_ReturnsInvalidArgument() {
        // Locks in the chain from audit #17 (store-side ArgumentOutOfRangeException
        // guard on `to < from`) through audit #32 (helper maps it to
        // StatusCode.InvalidArgument at the RPC boundary). Without the helper,
        // the exception surfaces as StatusCode.Internal, losing the "bad input"
        // signal for the client. All five query RPCs go through the same helper,
        // so one test exercises the shared code path.
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var request = new Local.GetAggregateTimelineRequest {
            FromUnixNs = FixedTimestamp.AddHours(1).ToUnixTimeMilliseconds() * 1_000_000,
            ToUnixNs = FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000,
            ResolutionMs = 1000,
        };

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _service.GetAggregateTimeline(request, context));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }
}
