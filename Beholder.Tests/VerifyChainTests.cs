using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Grpc;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Beholder.Protocol;
using Beholder.Tests.TestDoubles;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Local = Beholder.Protocol.Local;

namespace Beholder.Tests;

public sealed class VerifyChainTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly ConnectionFactory _connectionFactory;
    private readonly SqliteEventStore _eventStore;
    private readonly FakeTimeProvider _timeProvider;
    private readonly BroadcastService _broadcaster;
    private readonly FakeChainStatusCache _chainStatusCache;
    private readonly FakeCheckpointStore _checkpointStore;
    private readonly FakeCheckpointKeyProvider _keyProvider;
    private readonly BeholderLocalService _service;

    public VerifyChainTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();

        _connectionFactory = new ConnectionFactory(_databasePath, pooling: false);
        _timeProvider = new FakeTimeProvider(FixedTimestamp);
        _eventStore = new SqliteEventStore(_connectionFactory, _timeProvider);
        var firewallStore = new SqliteFirewallRuleStore(_connectionFactory);
        var alertStore = new SqliteAlertStore(_connectionFactory, NullLogger<SqliteAlertStore>.Instance);
        var snapshotSource = new FakeSnapshotBatchSource();
        _broadcaster = new BroadcastService(
            snapshotSource, _timeProvider, NullLogger<BroadcastService>.Instance);
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), _timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeRecordingSettingsState(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);

        _chainStatusCache = new FakeChainStatusCache();
        _checkpointStore = new FakeCheckpointStore();
        _keyProvider = new FakeCheckpointKeyProvider();

        _service = new BeholderLocalService(
            _broadcaster, pipeline, firewallStore, alertStore,
            new FakeFirewallController(), new FakeFirewallEnforcementState(),
            _eventStore, new FakeTrafficStore(),
            new FakeLanDeviceStore(), TestServiceFactory.CreateInactiveLanScannerService(),
            _chainStatusCache,
            new ChainVerifier(_eventStore, _checkpointStore,
                _keyProvider, NullLogger<ChainVerifier>.Instance),
            new FakeChainExporter(),
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
    public async Task VerifyChain_AnchoredRun_PopulatesAnchorSeqOnResponse() {
        // Phase 11.2: a seeded valid checkpoint makes the RPC anchor; the
        // response carries anchor_seq + anchor_key_id end-to-end through ToProto.
        await _eventStore.AppendAsync(EventKind.Counter, new byte[] { 0x01 }, CancellationToken.None);
        await _eventStore.AppendAsync(EventKind.NewProcess, new byte[] { 0x02 }, CancellationToken.None);
        await _eventStore.AppendAsync(EventKind.HashChanged, new byte[] { 0x03 }, CancellationToken.None);
        _checkpointStore.Seed(await SignValidCheckpointAtAsync(1));
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.VerifyChain(new Local.VerifyChainRequest(), context);

        Assert.True(response.IsValid);
        Assert.Equal(1, response.AnchorSeq);
        Assert.Equal(_keyProvider.KeyId, response.AnchorKeyId);
    }

    [Fact]
    public async Task VerifyChain_ForceFull_BypassesAnchor() {
        await _eventStore.AppendAsync(EventKind.Counter, new byte[] { 0x01 }, CancellationToken.None);
        await _eventStore.AppendAsync(EventKind.NewProcess, new byte[] { 0x02 }, CancellationToken.None);
        _checkpointStore.Seed(await SignValidCheckpointAtAsync(0));
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.VerifyChain(
            new Local.VerifyChainRequest { ForceFull = true }, context);

        Assert.True(response.IsValid);
        Assert.Equal(0, response.AnchorSeq);          // 0 = full walk, not anchored
        Assert.Empty(response.AnchorKeyId);
        Assert.Equal(2, response.RowsVerified);       // both rows walked from genesis
    }

    private async Task<Checkpoint> SignValidCheckpointAtAsync(long seq) {
        var rowHash = (await _eventStore.TryGetRowHashAsync(seq, CancellationToken.None))!;
        var payload = CheckpointSignaturePayload.Build(
            seq, rowHash, FixedTimestamp.ToUnixTimeMilliseconds() * 1_000_000L);
        var signature = _keyProvider.Sign(payload);
        return new Checkpoint(seq, rowHash, FixedTimestamp, signature, _keyProvider.KeyId);
    }

    [Fact]
    public async Task VerifyChain_EmptyChain_ReturnsValid() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.VerifyChain(new Local.VerifyChainRequest(), context);

        Assert.True(response.IsValid);
        Assert.Equal(0, response.RowsVerified);
        Assert.Equal(0, response.FailedAtSeq);
        Assert.Equal("", response.ErrorMessage);
    }

    [Fact]
    public async Task VerifyChain_UpdatesChainStatusCache_WithResult() {
        // Phase 13.1: the user-triggered VerifyChain RPC writes to the
        // same IChainStatusCache the periodic ChainIntegrityMonitor uses,
        // so the Settings tab's "last verified at" surfaces the most-
        // recent outcome regardless of which path produced it.
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        await _service.VerifyChain(new Local.VerifyChainRequest(), context);

        var update = Assert.Single(_chainStatusCache.UpdateCalls);
        Assert.True(update.Result.IsValid);
        Assert.Equal(FixedTimestamp, update.VerifiedAt);
    }

    [Fact]
    public async Task VerifyChain_ValidChain_ReturnsValid() {
        for (var i = 0; i < 3; i++) {
            await _eventStore.AppendAsync(EventKind.Counter, new byte[] { (byte)i }, CancellationToken.None);
        }
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.VerifyChain(new Local.VerifyChainRequest(), context);

        Assert.True(response.IsValid);
        Assert.Equal(3, response.RowsVerified);
    }

    [Fact]
    public async Task VerifyChain_CorruptedPayload_ReturnsInvalid() {
        for (var i = 0; i < 3; i++) {
            await _eventStore.AppendAsync(EventKind.Counter, new byte[] { (byte)i }, CancellationToken.None);
        }
        CorruptColumn(seq: 1L, column: "payload");
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.VerifyChain(new Local.VerifyChainRequest(), context);

        Assert.False(response.IsValid);
        Assert.Equal(1, response.FailedAtSeq);
        Assert.Contains("row_hash mismatch", response.ErrorMessage);
    }

    [Fact]
    public async Task VerifyChain_CorruptedRowHash_ReturnsInvalid() {
        for (var i = 0; i < 3; i++) {
            await _eventStore.AppendAsync(EventKind.Counter, new byte[] { (byte)i }, CancellationToken.None);
        }
        CorruptColumn(seq: 1L, column: "row_hash");
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.VerifyChain(new Local.VerifyChainRequest(), context);

        Assert.False(response.IsValid);
        Assert.Equal(1, response.FailedAtSeq);
        Assert.Contains("row_hash mismatch", response.ErrorMessage);
    }

    [Fact]
    public void ChainVerificationResult_ToProto_Success_UsesSentinels() {
        var result = ChainVerificationResult.Success(42);

        var proto = result.ToProto();

        Assert.True(proto.IsValid);
        Assert.Equal(42, proto.RowsVerified);
        Assert.Equal(0, proto.FailedAtSeq);
        Assert.Equal("", proto.ErrorMessage);
    }

    [Fact]
    public void ChainVerificationResult_ToProto_Failure_SetsFields() {
        var result = ChainVerificationResult.Failure(5, 3, "hash mismatch");

        var proto = result.ToProto();

        Assert.False(proto.IsValid);
        Assert.Equal(5, proto.RowsVerified);
        Assert.Equal(3, proto.FailedAtSeq);
        Assert.Equal("hash mismatch", proto.ErrorMessage);
    }

    [Fact]
    public async Task VerifyChain_VerifyAsyncThrows_ReturnsInternal() {
        var throwingStore = new ThrowingEventStore();
        var firewallStore = new SqliteFirewallRuleStore(_connectionFactory);
        var alertStore = new SqliteAlertStore(_connectionFactory, NullLogger<SqliteAlertStore>.Instance);
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), _timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeRecordingSettingsState(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);

        var service = new BeholderLocalService(
            _broadcaster, pipeline, firewallStore, alertStore,
            new FakeFirewallController(), new FakeFirewallEnforcementState(),
            throwingStore, new FakeTrafficStore(),
            new FakeLanDeviceStore(), TestServiceFactory.CreateInactiveLanScannerService(),
            new FakeChainStatusCache(),
            new ChainVerifier(throwingStore, new FakeCheckpointStore(),
                new FakeCheckpointKeyProvider(), NullLogger<ChainVerifier>.Instance),
            new FakeChainExporter(),
            new FakeStorageStatsProvider(),
            new FakeRecordingSettingsState(), new FakeHostnameResolutionSettingsState(),
            new FakeAlertSettingsState(),
            new FakeScannerSettingsState(),
            new FakeSettingsOverridesStore(),
            new FakeAppIdentityRuleStore(),
            _timeProvider, NullLogger<BeholderLocalService>.Instance);

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => service.VerifyChain(new Local.VerifyChainRequest(), context));

        Assert.Equal(StatusCode.Internal, ex.StatusCode);
    }

    private void CorruptColumn(long seq, string column) {
        if (column != "row_hash" && column != "prev_hash" && column != "payload")
            throw new ArgumentException($"Unsupported column for corruption: {column}", nameof(column));

        var garbage = column == "payload"
            ? new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }
            : new byte[32].Select(_ => (byte)0xEE).ToArray();

        using var connection = _connectionFactory.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE event_log SET {column} = $bytes WHERE seq = $seq;";
        command.Parameters.AddWithValue("$bytes", garbage);
        command.Parameters.AddWithValue("$seq", seq);
        command.ExecuteNonQuery();
    }

    private sealed class ThrowingEventStore : IEventStore {
        public Task<long> AppendAsync(EventKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated infrastructure failure");

        public Task<ChainVerificationResult> VerifyAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated infrastructure failure");

        public Task<IReadOnlyList<EventLogEntry>> ListByKindsAsync(
            IReadOnlyCollection<EventKind> kinds, int limit, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated infrastructure failure");

        public Task<ChainHead?> TryGetChainHeadAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated infrastructure failure");

        public Task<IReadOnlyList<EventLogRow>> ReadRangeAsync(
            long fromSeq, long toSeq, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated infrastructure failure");

        public Task<ChainVerificationResult> VerifyFromAsync(
            long fromSeq, byte[] expectedPrevHash, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated infrastructure failure");

        public Task<byte[]?> TryGetRowHashAsync(long seq, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated infrastructure failure");
    }
}
