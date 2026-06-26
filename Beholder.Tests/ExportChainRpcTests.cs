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

/// <summary>
/// Exercises the Phase 11.3 ExportChain RPC end-to-end through the real
/// BeholderLocalService + a real ChainExporter + SqliteEventStore, so the
/// signature in the returned envelope verifies against the daemon's key.
/// </summary>
public sealed class ExportChainRpcTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly ConnectionFactory _connectionFactory;
    private readonly SqliteEventStore _eventStore;
    private readonly FakeCheckpointKeyProvider _keyProvider;
    private readonly FakeTimeProvider _timeProvider;
    private readonly BroadcastService _broadcaster;
    private readonly BeholderLocalService _service;

    public ExportChainRpcTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        var databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(databasePath, pooling: false).Initialize();
        _connectionFactory = new ConnectionFactory(databasePath, pooling: false);
        _timeProvider = new FakeTimeProvider(FixedTimestamp);
        _eventStore = new SqliteEventStore(_connectionFactory, _timeProvider);
        _keyProvider = new FakeCheckpointKeyProvider();
        _broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(), _timeProvider, NullLogger<BroadcastService>.Instance);
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), _timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeRecordingSettingsState(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);

        _service = new BeholderLocalService(
            _broadcaster, pipeline,
            new FakeFirewallRuleStore(), new FakeAlertStore(),
            new FakeFirewallController(), new FakeFirewallEnforcementState(),
            _eventStore, new FakeTrafficStore(),
            new FakeLanDeviceStore(), TestServiceFactory.CreateInactiveLanScannerService(),
            new FakeChainStatusCache(), new FakeChainVerifier(),
            new ChainExporter(_keyProvider),
            new FakeStorageStatsProvider(),
            new FakeRecordingSettingsState(), new FakeHostnameResolutionSettingsState(),
            new FakeAlertSettingsState(),
            new FakeScannerSettingsState(),
            new FakeSettingsOverridesStore(),
            new FakeAppIdentityRuleStore(),
            _timeProvider, NullLogger<BeholderLocalService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
        _keyProvider.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ExportChain_FullChain_ReturnsSignedEnvelopeWithEventCount() {
        for (var i = 0; i < 4; i++) {
            await _eventStore.AppendAsync(EventKind.Counter, new byte[] { (byte)i }, CancellationToken.None);
        }
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.ExportChain(
            new Local.ExportChainRequest { FromSeq = 0, ToSeq = 0 }, context);

        Assert.Equal(4, response.EventCount);
        Assert.True(ChainExporter.TryVerify(response.SignedExport.Span));
    }

    [Fact]
    public async Task ExportChain_EmptyChain_ReturnsValidEnvelopeWithZeroEvents() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.ExportChain(
            new Local.ExportChainRequest { FromSeq = 0, ToSeq = 0 }, context);

        Assert.Equal(0, response.EventCount);
        Assert.True(ChainExporter.TryVerify(response.SignedExport.Span));
    }

    [Fact]
    public async Task ExportChain_SubRange_ReturnsOnlyMatchingEvents() {
        for (var i = 0; i < 6; i++) {
            await _eventStore.AppendAsync(EventKind.Counter, new byte[] { (byte)i }, CancellationToken.None);
        }
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.ExportChain(
            new Local.ExportChainRequest { FromSeq = 2, ToSeq = 4 }, context);

        Assert.Equal(3, response.EventCount);   // seqs 2, 3, 4
        Assert.True(ChainExporter.TryVerify(response.SignedExport.Span));
    }

    [Fact]
    public async Task ExportChain_FromGreaterThanTo_ReturnsInvalidArgument() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(() => _service.ExportChain(
            new Local.ExportChainRequest { FromSeq = 9, ToSeq = 3 }, context));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task ExportChain_NegativeSeq_ReturnsInvalidArgument() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(() => _service.ExportChain(
            new Local.ExportChainRequest { FromSeq = -1, ToSeq = 0 }, context));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }
}
