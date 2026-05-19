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

public sealed class ApplyFirewallRuleTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly SqliteFirewallRuleStore _firewallStore;
    private readonly SqliteEventStore _eventStore;
    private readonly FakeFirewallController _firewallController;
    private readonly FakeFirewallEnforcementState _enforcementState;
    private readonly FakeTimeProvider _timeProvider;
    private readonly FakeSnapshotBatchSource _snapshotSource;
    private readonly BroadcastService _broadcaster;
    private readonly BeholderLocalService _service;

    public ApplyFirewallRuleTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();

        var connectionFactory = new ConnectionFactory(_databasePath, pooling: false);
        _firewallStore = new SqliteFirewallRuleStore(connectionFactory);
        _timeProvider = new FakeTimeProvider(FixedTimestamp);
        _eventStore = new SqliteEventStore(connectionFactory, _timeProvider);
        _firewallController = new FakeFirewallController();
        _enforcementState = new FakeFirewallEnforcementState();
        _snapshotSource = new FakeSnapshotBatchSource();
        _broadcaster = new BroadcastService(
            _snapshotSource, _timeProvider, NullLogger<BroadcastService>.Instance);

        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), _timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeOptionsMonitor<RecordingOptions>(new RecordingOptions()),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);
        var alertStore = new SqliteAlertStore(connectionFactory, NullLogger<SqliteAlertStore>.Instance);

        _service = new BeholderLocalService(
            _broadcaster,
            pipeline,
            _firewallStore,
            alertStore,
            _firewallController,
            _enforcementState,
            _eventStore,
            new FakeTrafficStore(),
            new FakeLanDeviceStore(),
            TestServiceFactory.CreateInactiveLanScannerService(),
            _timeProvider,
            NullLogger<BeholderLocalService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
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
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeOptionsMonitor<RecordingOptions>(new RecordingOptions()),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);
        var connectionFactory = new ConnectionFactory(_databasePath, pooling: false);
        var alertStore = new SqliteAlertStore(connectionFactory, NullLogger<SqliteAlertStore>.Instance);

        var service = new BeholderLocalService(
            _broadcaster, pipeline, _firewallStore, alertStore,
            _firewallController, new FakeFirewallEnforcementState(),
            failingEventStore, new FakeTrafficStore(),
            new FakeLanDeviceStore(), TestServiceFactory.CreateInactiveLanScannerService(),
            _timeProvider, NullLogger<BeholderLocalService>.Instance);

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

    [Fact]
    public async Task ApplyFirewallRule_PersistFails_RollsBackOsRule() {
        var throwingStore = new ThrowingFirewallRuleStore();
        var firewallController = new FakeFirewallController();
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), _timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeOptionsMonitor<RecordingOptions>(new RecordingOptions()),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);
        var alertStore = new SqliteAlertStore(
            new ConnectionFactory(_databasePath, pooling: false), NullLogger<SqliteAlertStore>.Instance);

        var service = new BeholderLocalService(
            _broadcaster, pipeline, throwingStore, alertStore,
            firewallController, new FakeFirewallEnforcementState(),
            _eventStore, new FakeTrafficStore(),
            new FakeLanDeviceStore(), TestServiceFactory.CreateInactiveLanScannerService(),
            _timeProvider, NullLogger<BeholderLocalService>.Instance);

        var request = MakeRequest();
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => service.ApplyFirewallRule(request, context));

        Assert.Equal(StatusCode.Internal, ex.StatusCode);
        Assert.Single(firewallController.AddedRules);
        var removed = Assert.Single(firewallController.RemovedRules);
        Assert.Equal(@"C:\bin\curl.exe", removed.ProcessPath);
        Assert.Equal(Direction.Outbound, removed.Direction);
    }

    [Fact]
    public async Task ApplyFirewallRule_EnforcementOff_SkipsController() {
        // With master toggle OFF the daemon must not push the rule to the
        // OS firewall, even though SQLite + chain still record it. Reproduces
        // the bug where cycling a pill (BLOCK -> ALLOW -> BLOCK) while
        // FIREWALL: OFF still wrote to Windows Firewall.
        _enforcementState.SetEnabled(false);
        var request = MakeRequest();
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.ApplyFirewallRule(request, context);

        Assert.NotNull(response.Rule);
        Assert.Empty(_firewallController.AddedRules);
        Assert.Empty(_firewallController.RemovedRules);
    }

    [Fact]
    public async Task ApplyFirewallRule_EnforcementOff_StillPersistsAndChainAudits() {
        // The whole point of the enforcement-OFF mode: the user can configure
        // rules ahead of time and have them land in the OS when they flip
        // the master toggle back on. SQLite is the source of truth for that
        // replay (FirewallEnforcementService enumerates ListAllAsync), so
        // persistence + chain audit must run unconditionally.
        _enforcementState.SetEnabled(false);
        var request = MakeRequest();
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        await _service.ApplyFirewallRule(request, context);

        var persisted = await _firewallStore.ListAllAsync(CancellationToken.None);
        Assert.Single(persisted);
        Assert.Equal(@"C:\bin\curl.exe", persisted[0].ProcessPath);

        var chain = await _eventStore.VerifyAsync(CancellationToken.None);
        Assert.True(chain.IsValid);
        Assert.Equal(1, chain.RowsVerified);
    }

    private sealed class FailingEventStore : IEventStore {
        public Task<long> AppendAsync(EventKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated chain failure");

        public Task<ChainVerificationResult> VerifyAsync(CancellationToken cancellationToken)
            => Task.FromResult(ChainVerificationResult.Success(0));

        public Task<IReadOnlyList<EventLogEntry>> ListByKindsAsync(
            IReadOnlyCollection<EventKind> kinds, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EventLogEntry>>(Array.Empty<EventLogEntry>());
    }

    private sealed class ThrowingFirewallRuleStore : IFirewallRuleStore {
        public Task<FirewallRule> UpsertAsync(FirewallRule rule, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Simulated persist failure");

        public Task<FirewallRule?> GetByProcessAndDirectionAsync(
            string processPath, Direction direction, CancellationToken cancellationToken)
            => Task.FromResult<FirewallRule?>(null);

        public Task<IReadOnlyList<FirewallRule>> ListAllAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FirewallRule>>(Array.Empty<FirewallRule>());

        public Task<bool> RemoveAsync(string processPath, Direction direction, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
