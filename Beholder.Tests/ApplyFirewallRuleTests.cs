using Beholder.Core;
using Beholder.Daemon.Grpc;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Local = Beholder.Protocol.Local;

namespace Beholder.Tests;

public sealed class ApplyFirewallRuleTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly SqliteFirewallRuleStore _firewallStore;
    private readonly SqliteEventStore _eventStore;
    private readonly FakeFirewallController _firewallController;
    private readonly FakeTimeProvider _timeProvider;
    private readonly FakeSnapshotBatchSource _snapshotSource;
    private readonly BroadcastService _broadcaster;
    private readonly BeholderLocalService _service;

    public ApplyFirewallRuleTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath).Initialize();

        var connectionFactory = new ConnectionFactory(_databasePath);
        _firewallStore = new SqliteFirewallRuleStore(connectionFactory);
        _timeProvider = new FakeTimeProvider(FixedTimestamp);
        _eventStore = new SqliteEventStore(connectionFactory, _timeProvider);
        _firewallController = new FakeFirewallController();
        _snapshotSource = new FakeSnapshotBatchSource();
        _broadcaster = new BroadcastService(
            _snapshotSource, _timeProvider, NullLogger<BroadcastService>.Instance);

        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), _timeProvider,
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);
        var alertStore = new SqliteAlertStore(connectionFactory, NullLogger<SqliteAlertStore>.Instance);

        _service = new BeholderLocalService(
            _broadcaster,
            pipeline,
            _firewallStore,
            alertStore,
            _firewallController,
            _eventStore,
            _timeProvider,
            NullLogger<BeholderLocalService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ApplyFirewallRule_NewRule_CreatesAndChainLogs() {
        var request = MakeRequest();
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.ApplyFirewallRule(request, context);

        Assert.NotNull(response.Rule);
        Assert.True(response.Rule.Id > 0);
        Assert.Equal(@"C:\bin\curl.exe", response.Rule.ProcessPath);

        var added = Assert.Single(_firewallController.AddedRules);
        Assert.Equal(@"C:\bin\curl.exe", added.ProcessPath);

        var rules = await _firewallStore.ListAllAsync(CancellationToken.None);
        var rule = Assert.Single(rules);
        Assert.Equal(@"C:\bin\curl.exe", rule.ProcessPath);
        Assert.Equal(Direction.Outbound, rule.Direction);
        Assert.Equal(FirewallAction.Block, rule.Action);

        var chainResult = await _eventStore.VerifyAsync(CancellationToken.None);
        Assert.True(chainResult.IsValid);
        Assert.Equal(1, chainResult.RowsVerified);
    }

    [Fact]
    public async Task ApplyFirewallRule_ExistingRule_UpdatesAndEmitsChanged() {
        var initial = new FirewallRule(
            id: 0,
            processPath: @"C:\bin\curl.exe",
            direction: Direction.Outbound,
            action: FirewallAction.Allow,
            source: RuleSource.Manual,
            createdAt: FixedTimestamp,
            updatedAt: FixedTimestamp);
        await _firewallStore.UpsertAsync(initial, CancellationToken.None);

        _timeProvider.Advance(TimeSpan.FromHours(1));
        var request = MakeRequest(action: Local.FirewallAction.Block);
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.ApplyFirewallRule(request, context);

        var rules = await _firewallStore.ListAllAsync(CancellationToken.None);
        var rule = Assert.Single(rules);
        Assert.Equal(FirewallAction.Block, rule.Action);
        Assert.Equal(FixedTimestamp, rule.CreatedAt);
        Assert.Equal(FixedTimestamp.AddHours(1), rule.UpdatedAt);

        var chainResult = await _eventStore.VerifyAsync(CancellationToken.None);
        Assert.True(chainResult.IsValid);
        Assert.Equal(1, chainResult.RowsVerified);
    }

    [Fact]
    public async Task ApplyFirewallRule_EmptyProcessPath_ReturnsInvalidArgument() {
        var request = MakeRequest(processPath: "");
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _service.ApplyFirewallRule(request, context));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task ApplyFirewallRule_OsApplyFails_NothingPersisted() {
        _firewallController.AddRuleException = new InvalidOperationException("COM error");
        var request = MakeRequest();
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _service.ApplyFirewallRule(request, context));

        Assert.Equal(StatusCode.Internal, ex.StatusCode);

        var rules = await _firewallStore.ListAllAsync(CancellationToken.None);
        Assert.Empty(rules);

        var chainResult = await _eventStore.VerifyAsync(CancellationToken.None);
        Assert.Equal(0, chainResult.RowsVerified);
    }

    [Fact]
    public async Task ApplyFirewallRule_ChainAppendFails_StillReturnsSuccess() {
        var failingEventStore = new FailingEventStore();
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), _timeProvider,
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);
        var connectionFactory = new ConnectionFactory(_databasePath);
        var alertStore = new SqliteAlertStore(connectionFactory, NullLogger<SqliteAlertStore>.Instance);

        var service = new BeholderLocalService(
            _broadcaster, pipeline, _firewallStore, alertStore,
            _firewallController, failingEventStore, _timeProvider,
            NullLogger<BeholderLocalService>.Instance);

        var request = MakeRequest();
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await service.ApplyFirewallRule(request, context);

        Assert.NotNull(response.Rule);
        Assert.True(response.Rule.Id > 0);

        var rules = await _firewallStore.ListAllAsync(CancellationToken.None);
        Assert.Single(rules);
    }

    [Fact]
    public async Task ApplyFirewallRule_BroadcastsToSubscribers() {
        var ct = TestContext.Current.CancellationToken;
        await _broadcaster.StartAsync(ct);

        await using var enumerator = _broadcaster.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        var moveTask = enumerator.MoveNextAsync().AsTask();
        await WaitForAsync(() => _broadcaster.ActiveSubscriberCount == 1, "subscriber registered", ct);

        var request = MakeRequest();
        var context = new FakeServerCallContext(ct);
        await _service.ApplyFirewallRule(request, context);

        Assert.True(await moveTask.WaitAsync(WaitTimeout, ct));
        var daemonEvent = enumerator.Current;
        Assert.Equal(Local.DaemonEvent.PayloadOneofCase.RuleChange, daemonEvent.PayloadCase);
        Assert.Equal(Local.FirewallRuleChange.Types.ChangeKind.Created, daemonEvent.RuleChange.Change);
        Assert.Equal(@"C:\bin\curl.exe", daemonEvent.RuleChange.Rule.ProcessPath);

        await _broadcaster.StopAsync(ct);
    }

    private static Local.ApplyFirewallRuleRequest MakeRequest(
        string processPath = @"C:\bin\curl.exe",
        Local.Direction direction = Local.Direction.Outbound,
        Local.FirewallAction action = Local.FirewallAction.Block,
        Local.RuleSource source = Local.RuleSource.Manual
    ) => new() {
        ProcessPath = processPath,
        Direction = direction,
        Action = action,
        Source = source,
    };

    private static async Task WaitForAsync(
        Func<bool> predicate, string description, CancellationToken cancellationToken
    ) {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline) {
            await Task.Delay(10, cancellationToken);
        }
        if (!predicate()) throw new TimeoutException($"Timed out waiting for: {description}");
    }

    private sealed class FakeFirewallController : IFirewallController {
        public List<FirewallRule> AddedRules { get; } = new();
        public List<(string ProcessPath, Direction Direction)> RemovedRules { get; } = new();
        public Exception? AddRuleException { get; set; }
        public Exception? RemoveRuleException { get; set; }

        public Task AddRuleAsync(FirewallRule rule, CancellationToken cancellationToken) {
            if (AddRuleException is not null) throw AddRuleException;
            AddedRules.Add(rule);
            return Task.CompletedTask;
        }

        public Task RemoveRuleAsync(string processPath, Direction direction, CancellationToken cancellationToken) {
            if (RemoveRuleException is not null) throw RemoveRuleException;
            RemovedRules.Add((processPath, direction));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FirewallRule>> ListRulesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FirewallRule>>(Array.Empty<FirewallRule>());
    }

    private sealed class FailingEventStore : IEventStore {
        public Task AppendAsync(EventKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated chain failure");

        public Task<ChainVerificationResult> VerifyAsync(CancellationToken cancellationToken)
            => Task.FromResult(ChainVerificationResult.Success(0));
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

        public FakeServerCallContext(CancellationToken cancellationToken) {
            _cancellationToken = cancellationToken;
        }

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
