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

public sealed class GetProtocolBreakdownRpcTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly BroadcastService _broadcaster;
    private readonly BeholderLocalService _service;

    public GetProtocolBreakdownRpcTests() {
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
            new FakeOptionsMonitor<RecordingOptions>(new RecordingOptions()),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);

        _service = new BeholderLocalService(
            _broadcaster, pipeline, firewallStore, alertStore,
            new FakeFirewallController(), new FakeFirewallEnforcementState(),
            eventStore, trafficStore,
            timeProvider, NullLogger<BeholderLocalService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task GetProtocolBreakdown_InvertedRange_ReturnsInvalidArgument() {
        // The store-side guard (ArgumentOutOfRangeException on to < from) must
        // surface as StatusCode.InvalidArgument at the RPC boundary, via the
        // shared ExecuteQueryAsync helper. Matches the audit #32 pattern
        // already enforced on GetAggregateTimeline.
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var request = new Local.GetProtocolBreakdownRequest {
            FromUnixNs = FixedTimestamp.AddHours(1).ToUnixTimeMilliseconds() * 1_000_000,
            ToUnixNs = FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000,
        };

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _service.GetProtocolBreakdown(request, context));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }
}
