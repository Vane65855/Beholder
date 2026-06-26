using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Grpc;
using Beholder.Daemon.Pipeline;
using Beholder.Tests.TestDoubles;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Local = Beholder.Protocol.Local;

namespace Beholder.Tests;

public sealed class SetLanDeviceLabelRpcTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeLanDeviceStore _lanDeviceStore = new();
    private readonly BroadcastService _broadcaster;
    private readonly BeholderLocalService _service;

    public SetLanDeviceLabelRpcTests() {
        var timeProvider = new FakeTimeProvider(FixedTimestamp);
        _broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(), timeProvider, NullLogger<BroadcastService>.Instance);
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeRecordingSettingsState(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);

        _service = new BeholderLocalService(
            _broadcaster, pipeline,
            new FakeFirewallRuleStore(), new FakeAlertStore(),
            new FakeFirewallController(), new FakeFirewallEnforcementState(),
            new FakeEventStore(), new FakeTrafficStore(),
            _lanDeviceStore, TestServiceFactory.CreateInactiveLanScannerService(_lanDeviceStore),
            new FakeChainStatusCache(), new FakeChainVerifier(), new FakeChainExporter(), new FakeStorageStatsProvider(),
            new FakeRecordingSettingsState(), new FakeHostnameResolutionSettingsState(),
            new FakeAlertSettingsState(),
            new FakeScannerSettingsState(),
            new FakeSettingsOverridesStore(),
            new FakeAppIdentityRuleStore(),
            timeProvider, NullLogger<BeholderLocalService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
    }

    private void SeedDevice(string mac, string? existingLabel = null) {
        _lanDeviceStore.Seed(new LanDevice(
            Mac: mac,
            Ip: "192.168.1.10",
            Vendor: "Acme",
            Hostname: "router.lan",
            FirstSeen: FixedTimestamp.AddDays(-1),
            LastSeen: FixedTimestamp,
            Label: existingLabel));
    }

    [Fact]
    public async Task SetLanDeviceLabel_HappyPath_PersistsAndReturnsSuccess() {
        SeedDevice("aa:bb:cc:dd:ee:01");
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.SetLanDeviceLabel(
            new Local.SetLanDeviceLabelRequest { Mac = "aa:bb:cc:dd:ee:01", Label = "Living Room TV" },
            context);

        Assert.True(response.Success);
        Assert.Contains("Living Room TV", response.Message);
        var fetched = await _lanDeviceStore.GetByMacAsync("aa:bb:cc:dd:ee:01", CancellationToken.None);
        Assert.Equal("Living Room TV", fetched?.Label);
    }

    [Fact]
    public async Task SetLanDeviceLabel_EmptyMac_ThrowsInvalidArgument() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _service.SetLanDeviceLabel(
                new Local.SetLanDeviceLabelRequest { Mac = "", Label = "anything" }, context));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task SetLanDeviceLabel_LabelExceedsMaxLength_ReturnsSuccessFalse() {
        SeedDevice("aa:bb:cc:dd:ee:02");
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var tooLong = new string('a', 101);  // MaxLanDeviceLabelLength = 100
        var response = await _service.SetLanDeviceLabel(
            new Local.SetLanDeviceLabelRequest { Mac = "aa:bb:cc:dd:ee:02", Label = tooLong }, context);

        Assert.False(response.Success);
        Assert.Contains("100", response.Message);
        // The label must NOT have been persisted.
        var fetched = await _lanDeviceStore.GetByMacAsync("aa:bb:cc:dd:ee:02", CancellationToken.None);
        Assert.Null(fetched?.Label);
    }

    [Fact]
    public async Task SetLanDeviceLabel_UnknownMac_ReturnsSuccessFalseWithMessage() {
        // No device seeded for this MAC.
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.SetLanDeviceLabel(
            new Local.SetLanDeviceLabelRequest { Mac = "99:99:99:99:99:99", Label = "ghost" }, context);

        Assert.False(response.Success);
        Assert.Contains("99:99:99:99:99:99", response.Message);
    }

    [Fact]
    public async Task SetLanDeviceLabel_EmptyLabel_ClearsExistingLabel() {
        SeedDevice("aa:bb:cc:dd:ee:03", existingLabel: "Old Name");
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.SetLanDeviceLabel(
            new Local.SetLanDeviceLabelRequest { Mac = "aa:bb:cc:dd:ee:03", Label = "" }, context);

        Assert.True(response.Success);
        Assert.Contains("cleared", response.Message, StringComparison.OrdinalIgnoreCase);
        var fetched = await _lanDeviceStore.GetByMacAsync("aa:bb:cc:dd:ee:03", CancellationToken.None);
        Assert.Null(fetched?.Label);
    }

    [Fact]
    public async Task SetLanDeviceLabel_StoreThrows_ReturnsSuccessFalse() {
        var throwingStore = new ThrowingLanDeviceStore();
        using var broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(), TimeProvider.System, NullLogger<BroadcastService>.Instance);
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), TimeProvider.System,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeRecordingSettingsState(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);
        var service = new BeholderLocalService(
            broadcaster, pipeline,
            new FakeFirewallRuleStore(), new FakeAlertStore(),
            new FakeFirewallController(), new FakeFirewallEnforcementState(),
            new FakeEventStore(), new FakeTrafficStore(),
            throwingStore, TestServiceFactory.CreateInactiveLanScannerService(),
            new FakeChainStatusCache(), new FakeChainVerifier(), new FakeChainExporter(), new FakeStorageStatsProvider(),
            new FakeRecordingSettingsState(), new FakeHostnameResolutionSettingsState(),
            new FakeAlertSettingsState(),
            new FakeScannerSettingsState(),
            new FakeSettingsOverridesStore(),
            new FakeAppIdentityRuleStore(),
            TimeProvider.System, NullLogger<BeholderLocalService>.Instance);

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var response = await service.SetLanDeviceLabel(
            new Local.SetLanDeviceLabelRequest { Mac = "aa:bb:cc:dd:ee:04", Label = "boom" }, context);

        Assert.False(response.Success);
        Assert.Contains("simulated store failure", response.Message);
    }

    [Fact]
    public async Task SetLanDeviceLabel_OuterCancellation_PropagatesOperationCanceled() {
        // The cancellation must propagate to gRPC as Cancelled, not be
        // swallowed into success=false (mirror TriggerScan / ApplyFirewallRule
        // soft-failure-only-for-recoverable precedent).
        SeedDevice("aa:bb:cc:dd:ee:05");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var context = new FakeServerCallContext(cts.Token);

        var store = new CancellationProbingLanDeviceStore(cts.Token);
        using var broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(), TimeProvider.System, NullLogger<BroadcastService>.Instance);
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), TimeProvider.System,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeRecordingSettingsState(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);
        var service = new BeholderLocalService(
            broadcaster, pipeline,
            new FakeFirewallRuleStore(), new FakeAlertStore(),
            new FakeFirewallController(), new FakeFirewallEnforcementState(),
            new FakeEventStore(), new FakeTrafficStore(),
            store, TestServiceFactory.CreateInactiveLanScannerService(),
            new FakeChainStatusCache(), new FakeChainVerifier(), new FakeChainExporter(), new FakeStorageStatsProvider(),
            new FakeRecordingSettingsState(), new FakeHostnameResolutionSettingsState(),
            new FakeAlertSettingsState(),
            new FakeScannerSettingsState(),
            new FakeSettingsOverridesStore(),
            new FakeAppIdentityRuleStore(),
            TimeProvider.System, NullLogger<BeholderLocalService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SetLanDeviceLabel(
                new Local.SetLanDeviceLabelRequest { Mac = "aa:bb:cc:dd:ee:05", Label = "doomed" }, context));
    }

    private sealed class ThrowingLanDeviceStore : ILanDeviceStore {
        public Task<LanDevice?> GetByMacAsync(string mac, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated store failure");
        public Task<LanDevice?> GetByIpAsync(string ip, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated store failure");
        public Task<IReadOnlyList<LanDevice>> ListAsync(LanDeviceQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated store failure");
        public Task UpsertAsync(LanDevice device, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated store failure");
        public Task SetLabelAsync(string mac, string? label, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated store failure");
    }

    /// <summary>
    /// Store stub that throws <see cref="OperationCanceledException"/> when
    /// the injected token is cancelled â€” used to drive the RPC handler's
    /// cancellation-propagation path without needing a real store.
    /// </summary>
    private sealed class CancellationProbingLanDeviceStore : ILanDeviceStore {
        private readonly CancellationToken _watchToken;
        public CancellationProbingLanDeviceStore(CancellationToken watchToken) {
            _watchToken = watchToken;
        }
        public Task<LanDevice?> GetByMacAsync(string mac, CancellationToken cancellationToken) {
            _watchToken.ThrowIfCancellationRequested();
            return Task.FromResult<LanDevice?>(null);
        }
        public Task<LanDevice?> GetByIpAsync(string ip, CancellationToken cancellationToken) =>
            Task.FromResult<LanDevice?>(null);
        public Task<IReadOnlyList<LanDevice>> ListAsync(LanDeviceQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LanDevice>>([]);
        public Task UpsertAsync(LanDevice device, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetLabelAsync(string mac, string? label, CancellationToken cancellationToken) {
            _watchToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
