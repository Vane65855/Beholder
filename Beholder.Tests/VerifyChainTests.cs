using Beholder.Core;
using Beholder.Daemon.Grpc;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Beholder.Protocol;
using Grpc.Core;
using Microsoft.Data.Sqlite;
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
        new DatabaseInitializer(_databasePath).Initialize();

        _connectionFactory = new ConnectionFactory(_databasePath);
        _timeProvider = new FakeTimeProvider(FixedTimestamp);
        _eventStore = new SqliteEventStore(_connectionFactory, _timeProvider);
        var firewallStore = new SqliteFirewallRuleStore(_connectionFactory);
        var alertStore = new SqliteAlertStore(_connectionFactory, NullLogger<SqliteAlertStore>.Instance);
        var snapshotSource = new FakeSnapshotBatchSource();
        _broadcaster = new BroadcastService(
            snapshotSource, _timeProvider, NullLogger<BroadcastService>.Instance);
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), _timeProvider,
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);

        _service = new BeholderLocalService(
            _broadcaster, pipeline, firewallStore, alertStore,
            new FakeFirewallController(), _eventStore, _timeProvider,
            NullLogger<BeholderLocalService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
        SqliteConnection.ClearAllPools();
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

    private sealed class FakeFirewallController : IFirewallController {
        public Task AddRuleAsync(FirewallRule rule, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task RemoveRuleAsync(string processPath, Direction direction, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task<IReadOnlyList<FirewallRule>> ListRulesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FirewallRule>>(Array.Empty<FirewallRule>());
    }

    private sealed class FakeFlowSource : IFlowSource {
#pragma warning disable CS0067 // Event is required by IFlowSource but not exercised in these tests
        public event Action<FlowEvent>? OnFlowEvent;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSnapshotBatchSource : ISnapshotBatchSource {
        public event Action<IReadOnlyList<CounterSnapshot>>? OnSnapshotBatch;
        public void Fire(IReadOnlyList<CounterSnapshot> batch) => OnSnapshotBatch?.Invoke(batch);
    }

    private sealed class FakeServerCallContext : ServerCallContext {
        private readonly CancellationToken _cancellationToken;
        public FakeServerCallContext(CancellationToken cancellationToken) => _cancellationToken = cancellationToken;
        protected override string MethodCore => "/test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test-peer";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => _cancellationToken;
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore =>
            new(string.Empty, new Dictionary<string, List<AuthProperty>>());
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
            => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
            => Task.CompletedTask;
    }
}
