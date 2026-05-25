using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Grpc;
using Beholder.Daemon.Pipeline;
using Beholder.Tests.TestDoubles;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Local = Beholder.Protocol.Local;

namespace Beholder.Tests;

public sealed class GetStorageStatsRpcTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 5, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _timeProvider;
    private readonly BroadcastService _broadcaster;
    private readonly FakeStorageStatsProvider _storageStatsProvider;
    private readonly FakeChainStatusCache _chainStatusCache;
    private readonly BeholderLocalService _service;

    public GetStorageStatsRpcTests() {
        _timeProvider = new FakeTimeProvider(FixedTimestamp);
        _broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(), _timeProvider, NullLogger<BroadcastService>.Instance);
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), _timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeOptionsMonitor<RecordingOptions>(new RecordingOptions()),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);
        _storageStatsProvider = new FakeStorageStatsProvider();
        _chainStatusCache = new FakeChainStatusCache();

        _service = new BeholderLocalService(
            _broadcaster, pipeline,
            new FakeFirewallRuleStore(), new FakeAlertStore(),
            new FakeFirewallController(), new FakeFirewallEnforcementState(),
            new FakeEventStore(), new FakeTrafficStore(),
            new FakeLanDeviceStore(), TestServiceFactory.CreateInactiveLanScannerService(),
            _chainStatusCache, _storageStatsProvider,
            _timeProvider, NullLogger<BeholderLocalService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
    }

    [Fact]
    public async Task GetStorageStats_ReturnsTablesAndDatabaseSize() {
        _storageStatsProvider.Response = new StorageStats(
            DatabasePath: @"C:\daemon\data\beholder.db",
            DatabaseBytesTotal: 1024 * 1024 * 142,
            Tables: new[] {
                new TableStats("event_log", 100),
                new TableStats("traffic_raw", 50_000),
            },
            ChainStatus: null);
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.GetStorageStats(new Local.GetStorageStatsRequest(), context);

        Assert.Equal(@"C:\daemon\data\beholder.db", response.DatabasePath);
        Assert.Equal(1024 * 1024 * 142, response.DatabaseBytesTotal);
        Assert.Equal(2, response.Tables.Count);
        Assert.Equal("event_log", response.Tables[0].Name);
        Assert.Equal(100, response.Tables[0].RowCount);
        Assert.False(response.HasChainStatus);
    }

    [Fact]
    public async Task GetStorageStats_ChainStatusFlattenedOntoResponse() {
        var verifiedAt = new DateTimeOffset(2026, 5, 22, 14, 0, 0, TimeSpan.Zero);
        _storageStatsProvider.Response = new StorageStats(
            DatabasePath: "/var/lib/beholder/beholder.db",
            DatabaseBytesTotal: 100,
            Tables: Array.Empty<TableStats>(),
            ChainStatus: new ChainStatus(verifiedAt,
                ChainVerificationResult.Success(rowsVerified: 1247)));
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.GetStorageStats(new Local.GetStorageStatsRequest(), context);

        Assert.True(response.HasChainStatus);
        Assert.True(response.ChainStatus.IsValid);
        Assert.Equal(1247, response.ChainStatus.RowsVerified);
        Assert.Equal(verifiedAt.ToUnixTimeMilliseconds() * 1_000_000L,
            response.ChainStatus.LastVerifiedUnixNs);
    }

    [Fact]
    public async Task GetStorageStats_ProviderThrows_SurfacesAsInternal() {
        _storageStatsProvider.Exception = new IOException("disk full");
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _service.GetStorageStats(new Local.GetStorageStatsRequest(), context));

        Assert.Equal(StatusCode.Internal, ex.StatusCode);
    }

    [Fact]
    public async Task GetStorageStats_Cancellation_PropagatesAsOperationCanceled() {
        // The provider's stub honours the cancellation token via
        // ThrowIfCancellationRequested before returning.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var context = new FakeServerCallContext(cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.GetStorageStats(new Local.GetStorageStatsRequest(), context));
    }
}
