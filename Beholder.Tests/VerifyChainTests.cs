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
            new TrafficStorageOptions(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);

        _service = new BeholderLocalService(
            _broadcaster, pipeline, firewallStore, alertStore,
            new FakeFirewallController(), _eventStore, new FakeTrafficStore(),
            _timeProvider, NullLogger<BeholderLocalService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
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
            new TrafficStorageOptions(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);

        var service = new BeholderLocalService(
            _broadcaster, pipeline, firewallStore, alertStore,
            new FakeFirewallController(), throwingStore, new FakeTrafficStore(),
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
        public Task AppendAsync(EventKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated infrastructure failure");

        public Task<ChainVerificationResult> VerifyAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated infrastructure failure");
    }
}
