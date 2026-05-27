using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Grpc;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Local = Beholder.Protocol.Local;

namespace Beholder.Tests;

public sealed class ListFirewallRulesRpcTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly SqliteFirewallRuleStore _firewallStore;
    private readonly BroadcastService _broadcaster;
    private readonly BeholderLocalService _service;

    public ListFirewallRulesRpcTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        var databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(databasePath, pooling: false).Initialize();

        var connectionFactory = new ConnectionFactory(databasePath, pooling: false);
        _firewallStore = new SqliteFirewallRuleStore(connectionFactory);
        var timeProvider = new FakeTimeProvider(FixedTimestamp);
        var eventStore = new SqliteEventStore(connectionFactory, timeProvider);
        var alertStore = new SqliteAlertStore(connectionFactory, NullLogger<SqliteAlertStore>.Instance);
        _broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(), timeProvider, NullLogger<BroadcastService>.Instance);
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeRecordingSettingsState(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);

        _service = new BeholderLocalService(
            _broadcaster, pipeline, _firewallStore, alertStore,
            new FakeFirewallController(), new FakeFirewallEnforcementState(),
            eventStore, new FakeTrafficStore(),
            new FakeLanDeviceStore(), TestServiceFactory.CreateInactiveLanScannerService(),
            new FakeChainStatusCache(), new FakeStorageStatsProvider(),
            new FakeRecordingSettingsState(), new FakeHostnameResolutionSettingsState(),
            new FakeAlertSettingsState(),
            new FakeScannerSettingsState(),
            new FakeSettingsOverridesStore(),
            new FakeAppIdentityRuleStore(),
            timeProvider, NullLogger<BeholderLocalService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ListFirewallRules_EmptyStore_ReturnsEmpty() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.ListFirewallRules(new Local.ListFirewallRulesRequest(), context);

        Assert.Empty(response.Rules);
    }

    [Fact]
    public async Task ListFirewallRules_PopulatedStore_ReturnsAllRules() {
        await _firewallStore.UpsertAsync(new FirewallRule(
            id: 0, processPath: @"C:\a.exe",
            direction: Direction.Outbound, action: FirewallAction.Block,
            source: RuleSource.Manual, createdAt: FixedTimestamp, updatedAt: FixedTimestamp),
            CancellationToken.None);
        await _firewallStore.UpsertAsync(new FirewallRule(
            id: 0, processPath: @"C:\b.exe",
            direction: Direction.Inbound, action: FirewallAction.Allow,
            source: RuleSource.Default, createdAt: FixedTimestamp, updatedAt: FixedTimestamp),
            CancellationToken.None);

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var response = await _service.ListFirewallRules(new Local.ListFirewallRulesRequest(), context);

        Assert.Equal(2, response.Rules.Count);
        var paths = response.Rules.Select(r => r.ProcessPath).ToHashSet();
        Assert.Contains(@"C:\a.exe", paths);
        Assert.Contains(@"C:\b.exe", paths);
    }

    [Fact]
    public async Task ListFirewallRules_PreservesIdAndDirection() {
        var inserted = await _firewallStore.UpsertAsync(new FirewallRule(
            id: 0, processPath: @"C:\bin\curl.exe",
            direction: Direction.Outbound, action: FirewallAction.Block,
            source: RuleSource.Manual, createdAt: FixedTimestamp, updatedAt: FixedTimestamp),
            CancellationToken.None);

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var response = await _service.ListFirewallRules(new Local.ListFirewallRulesRequest(), context);

        var single = Assert.Single(response.Rules);
        Assert.Equal(inserted.Id, single.Id);
        Assert.Equal(Local.Direction.Outbound, single.Direction);
        Assert.Equal(Local.FirewallAction.Block, single.Action);
        Assert.Equal(Local.RuleSource.Manual, single.Source);
    }

    [Fact]
    public async Task ListFirewallRules_OrderedById() {
        // Insert in non-monotonic process_path order to confirm the response
        // ordering follows IFirewallRuleStore.ListAllAsync (id-asc) regardless
        // of insertion order or path collation.
        await _firewallStore.UpsertAsync(new FirewallRule(
            id: 0, processPath: @"C:\zzz.exe",
            direction: Direction.Outbound, action: FirewallAction.Block,
            source: RuleSource.Manual, createdAt: FixedTimestamp, updatedAt: FixedTimestamp),
            CancellationToken.None);
        await _firewallStore.UpsertAsync(new FirewallRule(
            id: 0, processPath: @"C:\aaa.exe",
            direction: Direction.Outbound, action: FirewallAction.Allow,
            source: RuleSource.Manual, createdAt: FixedTimestamp, updatedAt: FixedTimestamp),
            CancellationToken.None);

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var response = await _service.ListFirewallRules(new Local.ListFirewallRulesRequest(), context);

        Assert.Equal(2, response.Rules.Count);
        Assert.True(response.Rules[0].Id < response.Rules[1].Id);
        Assert.Equal(@"C:\zzz.exe", response.Rules[0].ProcessPath);
        Assert.Equal(@"C:\aaa.exe", response.Rules[1].ProcessPath);
    }
}
