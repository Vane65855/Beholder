using Beholder.Daemon;
using Beholder.Daemon.Grpc;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Local = Beholder.Protocol.Local;

namespace Beholder.Tests;

public sealed class SetFirewallEnabledRpcTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly SqliteEventStore _eventStore;
    private readonly FakeFirewallEnforcementState _enforcementState;
    private readonly BroadcastService _broadcaster;
    private readonly BeholderLocalService _service;

    public SetFirewallEnabledRpcTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        var databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(databasePath, pooling: false).Initialize();

        var connectionFactory = new ConnectionFactory(databasePath, pooling: false);
        var firewallStore = new SqliteFirewallRuleStore(connectionFactory);
        var alertStore = new SqliteAlertStore(connectionFactory, NullLogger<SqliteAlertStore>.Instance);
        var timeProvider = new FakeTimeProvider(FixedTimestamp);
        _eventStore = new SqliteEventStore(connectionFactory, timeProvider);
        _enforcementState = new FakeFirewallEnforcementState(initialEnabled: true);
        _broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(), timeProvider, NullLogger<BroadcastService>.Instance);
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeRecordingSettingsState(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);

        _service = new BeholderLocalService(
            _broadcaster, pipeline, firewallStore, alertStore,
            new FakeFirewallController(), _enforcementState,
            _eventStore, new FakeTrafficStore(),
            new FakeLanDeviceStore(), TestServiceFactory.CreateInactiveLanScannerService(),
            new FakeChainStatusCache(), new FakeStorageStatsProvider(),
            new FakeRecordingSettingsState(), new FakeHostnameResolutionSettingsState(),
            new FakeAlertSettingsState(),
            new FakeSettingsOverridesStore(),
            timeProvider, NullLogger<BeholderLocalService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task SetFirewallEnabled_ToggleOff_FlipsState() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.SetFirewallEnabled(
            new Local.SetFirewallEnabledRequest { Enabled = false }, context);

        Assert.False(response.Enabled);
        Assert.False(_enforcementState.Enabled);
    }

    [Fact]
    public async Task SetFirewallEnabled_ToggleOn_FlipsState() {
        _enforcementState.SetEnabled(false);
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.SetFirewallEnabled(
            new Local.SetFirewallEnabledRequest { Enabled = true }, context);

        Assert.True(response.Enabled);
        Assert.True(_enforcementState.Enabled);
    }

    [Fact]
    public async Task SetFirewallEnabled_NoOpWhenAlreadyEnabled_DoesNotAppendChain() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        // State starts enabled; setting enabled=true is a no-op.
        var response = await _service.SetFirewallEnabled(
            new Local.SetFirewallEnabledRequest { Enabled = true }, context);

        Assert.True(response.Enabled);
        var verification = await _eventStore.VerifyAsync(CancellationToken.None);
        Assert.Equal(0, verification.RowsVerified);
    }

    [Fact]
    public async Task SetFirewallEnabled_RealTransition_AppendsChainEntry() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        await _service.SetFirewallEnabled(
            new Local.SetFirewallEnabledRequest { Enabled = false }, context);

        var verification = await _eventStore.VerifyAsync(CancellationToken.None);
        Assert.True(verification.IsValid);
        Assert.Equal(1, verification.RowsVerified);
    }

    [Fact]
    public async Task SetFirewallEnabled_TwoToggles_AppendsTwoChainEntries() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        await _service.SetFirewallEnabled(
            new Local.SetFirewallEnabledRequest { Enabled = false }, context);
        await _service.SetFirewallEnabled(
            new Local.SetFirewallEnabledRequest { Enabled = true }, context);

        var verification = await _eventStore.VerifyAsync(CancellationToken.None);
        Assert.Equal(2, verification.RowsVerified);
    }

    [Fact]
    public async Task SetFirewallEnabled_RaisesStateChangedOnTransition() {
        var seen = new List<bool>();
        _enforcementState.StateChanged += value => seen.Add(value);
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        await _service.SetFirewallEnabled(
            new Local.SetFirewallEnabledRequest { Enabled = false }, context);

        Assert.Single(seen);
        Assert.False(seen[0]);
    }

    [Fact]
    public async Task SetFirewallEnabled_DoesNotRaiseOnNoOp() {
        var seen = new List<bool>();
        _enforcementState.StateChanged += value => seen.Add(value);
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        // Already enabled; re-asserting must not fire the event.
        await _service.SetFirewallEnabled(
            new Local.SetFirewallEnabledRequest { Enabled = true }, context);

        Assert.Empty(seen);
    }

    [Fact]
    public async Task GetSnapshot_ReflectsEnforcementStateTrue() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.GetSnapshot(new Local.GetSnapshotRequest(), context);

        Assert.True(response.FirewallEnforcementEnabled);
    }

    [Fact]
    public async Task GetSnapshot_ReflectsEnforcementStateFalse() {
        _enforcementState.SetEnabled(false);
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.GetSnapshot(new Local.GetSnapshotRequest(), context);

        Assert.False(response.FirewallEnforcementEnabled);
    }
}
