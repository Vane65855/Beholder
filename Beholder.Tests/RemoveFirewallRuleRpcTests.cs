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

public sealed class RemoveFirewallRuleRpcTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    private readonly string _tempDir;
    private readonly SqliteFirewallRuleStore _firewallStore;
    private readonly SqliteEventStore _eventStore;
    private readonly FakeFirewallController _firewallController;
    private readonly FakeFirewallEnforcementState _enforcementState;
    private readonly FakeTimeProvider _timeProvider;
    private readonly BroadcastService _broadcaster;
    private readonly BeholderLocalService _service;

    public RemoveFirewallRuleRpcTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        var databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(databasePath, pooling: false).Initialize();

        var connectionFactory = new ConnectionFactory(databasePath, pooling: false);
        _firewallStore = new SqliteFirewallRuleStore(connectionFactory);
        _timeProvider = new FakeTimeProvider(FixedTimestamp);
        _eventStore = new SqliteEventStore(connectionFactory, _timeProvider);
        _firewallController = new FakeFirewallController();
        _enforcementState = new FakeFirewallEnforcementState();
        _broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(), _timeProvider, NullLogger<BroadcastService>.Instance);

        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), _timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeRecordingSettingsState(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);
        var alertStore = new SqliteAlertStore(connectionFactory, NullLogger<SqliteAlertStore>.Instance);

        _service = new BeholderLocalService(
            _broadcaster, pipeline, _firewallStore, alertStore,
            _firewallController, _enforcementState,
            _eventStore, new FakeTrafficStore(),
            new FakeLanDeviceStore(), TestServiceFactory.CreateInactiveLanScannerService(),
            new FakeChainStatusCache(), new FakeChainVerifier(), new FakeChainExporter(), new FakeStorageStatsProvider(),
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
    public async Task RemoveFirewallRule_EmptyProcessPath_ReturnsInvalidArgument() {
        var request = new Local.RemoveFirewallRuleRequest {
            ProcessPath = "",
            Direction = Local.Direction.Outbound,
        };
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _service.RemoveFirewallRule(request, context));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task RemoveFirewallRule_NonExistentRule_ReturnsRemovedFalse() {
        var request = new Local.RemoveFirewallRuleRequest {
            ProcessPath = @"C:\bin\never-existed.exe",
            Direction = Local.Direction.Outbound,
        };
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.RemoveFirewallRule(request, context);

        Assert.False(response.Removed);
        // Idempotent: still passes through to the controller in case OS state
        // is ahead of SQLite. FakeFirewallController records the call.
        var removed = Assert.Single(_firewallController.RemovedRules);
        Assert.Equal(@"C:\bin\never-existed.exe", removed.ProcessPath);
    }

    [Fact]
    public async Task RemoveFirewallRule_ExistingRule_RemovesFromOsAndStore() {
        var existing = await _firewallStore.UpsertAsync(new FirewallRule(
            id: 0, processPath: @"C:\bin\curl.exe",
            direction: Direction.Outbound,
            action: FirewallAction.Block,
            source: RuleSource.Manual,
            createdAt: FixedTimestamp, updatedAt: FixedTimestamp), CancellationToken.None);

        var request = new Local.RemoveFirewallRuleRequest {
            ProcessPath = @"C:\bin\curl.exe",
            Direction = Local.Direction.Outbound,
        };
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.RemoveFirewallRule(request, context);

        Assert.True(response.Removed);
        Assert.Equal(existing.Id, response.Rule.Id);
        Assert.Equal(@"C:\bin\curl.exe", response.Rule.ProcessPath);

        var removed = Assert.Single(_firewallController.RemovedRules);
        Assert.Equal(@"C:\bin\curl.exe", removed.ProcessPath);
        Assert.Equal(Direction.Outbound, removed.Direction);

        var afterRules = await _firewallStore.ListAllAsync(CancellationToken.None);
        Assert.Empty(afterRules);
    }

    [Fact]
    public async Task RemoveFirewallRule_ExistingRule_AppendsChainEntry() {
        await _firewallStore.UpsertAsync(new FirewallRule(
            id: 0, processPath: @"C:\bin\curl.exe",
            direction: Direction.Outbound,
            action: FirewallAction.Block,
            source: RuleSource.Manual,
            createdAt: FixedTimestamp, updatedAt: FixedTimestamp), CancellationToken.None);

        var request = new Local.RemoveFirewallRuleRequest {
            ProcessPath = @"C:\bin\curl.exe",
            Direction = Local.Direction.Outbound,
        };
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        await _service.RemoveFirewallRule(request, context);

        var verification = await _eventStore.VerifyAsync(CancellationToken.None);
        Assert.True(verification.IsValid);
        Assert.Equal(1, verification.RowsVerified);
    }

    [Fact]
    public async Task RemoveFirewallRule_BroadcastsRemovedEvent() {
        await _firewallStore.UpsertAsync(new FirewallRule(
            id: 0, processPath: @"C:\bin\curl.exe",
            direction: Direction.Outbound,
            action: FirewallAction.Block,
            source: RuleSource.Manual,
            createdAt: FixedTimestamp, updatedAt: FixedTimestamp), CancellationToken.None);

        var ct = TestContext.Current.CancellationToken;
        await _broadcaster.StartAsync(ct);
        await using var enumerator = _broadcaster.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        var moveTask = enumerator.MoveNextAsync().AsTask();
        await WaitForAsync(() => _broadcaster.ActiveSubscriberCount == 1, "subscriber registered", ct);

        var request = new Local.RemoveFirewallRuleRequest {
            ProcessPath = @"C:\bin\curl.exe",
            Direction = Local.Direction.Outbound,
        };
        var context = new FakeServerCallContext(ct);
        await _service.RemoveFirewallRule(request, context);

        Assert.True(await moveTask.WaitAsync(WaitTimeout, ct));
        var daemonEvent = enumerator.Current;
        Assert.Equal(Local.DaemonEvent.PayloadOneofCase.RuleChange, daemonEvent.PayloadCase);
        Assert.Equal(Local.FirewallRuleChange.Types.ChangeKind.Removed, daemonEvent.RuleChange.Change);
        Assert.Equal(@"C:\bin\curl.exe", daemonEvent.RuleChange.Rule.ProcessPath);

        await _broadcaster.StopAsync(ct);
    }

    [Fact]
    public async Task RemoveFirewallRule_ControllerThrows_DoesNotPersistDelete() {
        await _firewallStore.UpsertAsync(new FirewallRule(
            id: 0, processPath: @"C:\bin\curl.exe",
            direction: Direction.Outbound,
            action: FirewallAction.Block,
            source: RuleSource.Manual,
            createdAt: FixedTimestamp, updatedAt: FixedTimestamp), CancellationToken.None);

        _firewallController.RemoveRuleException =
            new InvalidOperationException("Simulated OS-level failure");

        var request = new Local.RemoveFirewallRuleRequest {
            ProcessPath = @"C:\bin\curl.exe",
            Direction = Local.Direction.Outbound,
        };
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _service.RemoveFirewallRule(request, context));
        Assert.Equal(StatusCode.Internal, ex.StatusCode);

        // SQLite must still hold the rule â€” controller failure aborts before
        // persistence, mirroring ApplyFirewallRule's "OS leads" semantics.
        var afterRules = await _firewallStore.ListAllAsync(CancellationToken.None);
        Assert.Single(afterRules);
    }

    [Fact]
    public async Task RemoveFirewallRule_DoubleRemove_IdempotentSecondCall() {
        await _firewallStore.UpsertAsync(new FirewallRule(
            id: 0, processPath: @"C:\bin\curl.exe",
            direction: Direction.Outbound,
            action: FirewallAction.Block,
            source: RuleSource.Manual,
            createdAt: FixedTimestamp, updatedAt: FixedTimestamp), CancellationToken.None);

        var request = new Local.RemoveFirewallRuleRequest {
            ProcessPath = @"C:\bin\curl.exe",
            Direction = Local.Direction.Outbound,
        };
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var first = await _service.RemoveFirewallRule(request, context);
        var second = await _service.RemoveFirewallRule(request, context);

        Assert.True(first.Removed);
        Assert.False(second.Removed);
    }

    [Fact]
    public async Task RemoveFirewallRule_EnforcementOff_SkipsController() {
        // Seed an existing rule so the handler hits the main remove path
        // (not the idempotent early-return for "no persisted rule").
        await _firewallStore.UpsertAsync(new FirewallRule(
            id: 0, processPath: @"C:\bin\curl.exe",
            direction: Direction.Outbound,
            action: FirewallAction.Block,
            source: RuleSource.Manual,
            createdAt: FixedTimestamp, updatedAt: FixedTimestamp), CancellationToken.None);

        _enforcementState.SetEnabled(false);
        var request = new Local.RemoveFirewallRuleRequest {
            ProcessPath = @"C:\bin\curl.exe",
            Direction = Local.Direction.Outbound,
        };
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.RemoveFirewallRule(request, context);

        Assert.True(response.Removed);
        // OS-firewall must stay untouched while enforcement is off.
        Assert.Empty(_firewallController.RemovedRules);
    }

    [Fact]
    public async Task RemoveFirewallRule_EnforcementOff_StillDeletesFromStore() {
        // SQLite is the source of truth for the FirewallEnforcementService
        // replay-on-toggle-on path. A rule removed while enforcement is off
        // must still leave SQLite, otherwise toggling back on would resurrect
        // the rule by re-applying the now-stale persisted copy.
        await _firewallStore.UpsertAsync(new FirewallRule(
            id: 0, processPath: @"C:\bin\curl.exe",
            direction: Direction.Outbound,
            action: FirewallAction.Block,
            source: RuleSource.Manual,
            createdAt: FixedTimestamp, updatedAt: FixedTimestamp), CancellationToken.None);

        _enforcementState.SetEnabled(false);
        var request = new Local.RemoveFirewallRuleRequest {
            ProcessPath = @"C:\bin\curl.exe",
            Direction = Local.Direction.Outbound,
        };
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        await _service.RemoveFirewallRule(request, context);

        var rules = await _firewallStore.ListAllAsync(CancellationToken.None);
        Assert.Empty(rules);
    }

    private static async Task WaitForAsync(
        Func<bool> predicate, string description, CancellationToken cancellationToken
    ) {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline) {
            await Task.Delay(10, cancellationToken);
        }
        if (!predicate()) throw new TimeoutException($"Timed out waiting for: {description}");
    }
}
