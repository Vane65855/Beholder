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

public sealed class GetFirewallActivityRpcTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly SqliteFirewallRuleStore _firewallStore;
    private readonly SqliteEventStore _eventStore;
    private readonly FakeFirewallController _firewallController;
    private readonly FakeFirewallEnforcementState _enforcementState;
    private readonly BroadcastService _broadcaster;
    private readonly BeholderLocalService _service;
    private readonly FakeTimeProvider _timeProvider;

    public GetFirewallActivityRpcTests() {
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
    public async Task GetFirewallActivity_EmptyChain_ReturnsEmpty() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.GetFirewallActivity(
            new Local.GetFirewallActivityRequest { Limit = 100 }, context);

        Assert.Empty(response.Events);
    }

    [Fact]
    public async Task GetFirewallActivity_AfterApplyAndRemove_ReturnsBothEntries() {
        var ct = TestContext.Current.CancellationToken;
        await _service.ApplyFirewallRule(new Local.ApplyFirewallRuleRequest {
            ProcessPath = @"C:\bin\curl.exe",
            Direction = Local.Direction.Outbound,
            Action = Local.FirewallAction.Block,
            Source = Local.RuleSource.Manual,
        }, new FakeServerCallContext(ct));
        await _service.RemoveFirewallRule(new Local.RemoveFirewallRuleRequest {
            ProcessPath = @"C:\bin\curl.exe",
            Direction = Local.Direction.Outbound,
        }, new FakeServerCallContext(ct));

        var response = await _service.GetFirewallActivity(
            new Local.GetFirewallActivityRequest { Limit = 100 }, new FakeServerCallContext(ct));

        Assert.Equal(2, response.Events.Count);
        // Newest-first: removal landed second, so seq is higher.
        Assert.Equal(Local.FirewallActivityKind.RuleRemoved, response.Events[0].Kind);
        Assert.Equal(Local.FirewallActivityKind.RuleCreated, response.Events[1].Kind);
        Assert.True(response.Events[0].Seq > response.Events[1].Seq);
    }

    [Fact]
    public async Task GetFirewallActivity_DecodesRuleProcessPath() {
        var ct = TestContext.Current.CancellationToken;
        await _service.ApplyFirewallRule(new Local.ApplyFirewallRuleRequest {
            ProcessPath = @"C:\bin\firefox.exe",
            Direction = Local.Direction.Inbound,
            Action = Local.FirewallAction.Allow,
            Source = Local.RuleSource.Manual,
        }, new FakeServerCallContext(ct));

        var response = await _service.GetFirewallActivity(
            new Local.GetFirewallActivityRequest { Limit = 10 }, new FakeServerCallContext(ct));

        var ev = Assert.Single(response.Events);
        Assert.Equal(@"C:\bin\firefox.exe", ev.ProcessPath);
        Assert.Equal(Local.Direction.Inbound, ev.Direction);
        Assert.Equal(Local.FirewallAction.Allow, ev.Action);
    }

    [Fact]
    public async Task GetFirewallActivity_AfterEnforcementToggle_DecodesEnabledFlag() {
        var ct = TestContext.Current.CancellationToken;
        await _service.SetFirewallEnabled(
            new Local.SetFirewallEnabledRequest { Enabled = false }, new FakeServerCallContext(ct));

        var response = await _service.GetFirewallActivity(
            new Local.GetFirewallActivityRequest { Limit = 10 }, new FakeServerCallContext(ct));

        var ev = Assert.Single(response.Events);
        Assert.Equal(Local.FirewallActivityKind.EnforcementToggled, ev.Kind);
        Assert.False(ev.EnforcementEnabled);
    }

    [Fact]
    public async Task GetFirewallActivity_LimitClampedAtServerCap() {
        // 600 > hard cap (500) â€” server clamps without throwing.
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.GetFirewallActivity(
            new Local.GetFirewallActivityRequest { Limit = 600 }, context);

        // Empty chain â†’ still empty. Validates that the clamp doesn't reject
        // the call.
        Assert.Empty(response.Events);
    }

    [Fact]
    public async Task GetFirewallActivity_ZeroLimit_UsesServerDefault() {
        var ct = TestContext.Current.CancellationToken;
        // Add 5 events so we can verify the default is non-zero.
        for (var i = 0; i < 5; i++) {
            await _service.ApplyFirewallRule(new Local.ApplyFirewallRuleRequest {
                ProcessPath = $@"C:\app{i}.exe",
                Direction = Local.Direction.Outbound,
                Action = Local.FirewallAction.Block,
                Source = Local.RuleSource.Manual,
            }, new FakeServerCallContext(ct));
        }

        var response = await _service.GetFirewallActivity(
            new Local.GetFirewallActivityRequest { Limit = 0 }, new FakeServerCallContext(ct));

        Assert.Equal(5, response.Events.Count);
    }

    [Fact]
    public async Task GetFirewallActivity_NegativeLimit_ReturnsInvalidArgument() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _service.GetFirewallActivity(
                new Local.GetFirewallActivityRequest { Limit = -1 }, context));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task GetFirewallActivity_OnlyReturnsFirewallKinds() {
        var ct = TestContext.Current.CancellationToken;
        // Append a non-firewall event directly to the chain.
        await _eventStore.AppendAsync(EventKind.Counter, new byte[] { 0x01 }, ct);
        await _service.ApplyFirewallRule(new Local.ApplyFirewallRuleRequest {
            ProcessPath = @"C:\app.exe",
            Direction = Local.Direction.Outbound,
            Action = Local.FirewallAction.Block,
            Source = Local.RuleSource.Manual,
        }, new FakeServerCallContext(ct));

        var response = await _service.GetFirewallActivity(
            new Local.GetFirewallActivityRequest { Limit = 100 }, new FakeServerCallContext(ct));

        // Only the firewall event should appear; the counter is filtered out.
        var ev = Assert.Single(response.Events);
        Assert.Equal(Local.FirewallActivityKind.RuleCreated, ev.Kind);
    }

    [Fact]
    public async Task GetFirewallActivity_LimitTrimsResults() {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 10; i++) {
            await _service.ApplyFirewallRule(new Local.ApplyFirewallRuleRequest {
                ProcessPath = $@"C:\app{i}.exe",
                Direction = Local.Direction.Outbound,
                Action = Local.FirewallAction.Block,
                Source = Local.RuleSource.Manual,
            }, new FakeServerCallContext(ct));
        }

        var response = await _service.GetFirewallActivity(
            new Local.GetFirewallActivityRequest { Limit = 3 }, new FakeServerCallContext(ct));

        Assert.Equal(3, response.Events.Count);
    }
}
