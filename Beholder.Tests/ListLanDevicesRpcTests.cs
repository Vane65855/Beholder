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

public sealed class ListLanDevicesRpcTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 5, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeLanDeviceStore _lanDeviceStore = new();
    private readonly BroadcastService _broadcaster;
    private readonly BeholderLocalService _service;

    public ListLanDevicesRpcTests() {
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

    [Fact]
    public async Task ListLanDevices_EmptyStore_ReturnsEmptyList() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var response = await _service.ListLanDevices(new Local.ListLanDevicesRequest(), context);

        Assert.Empty(response.Devices);
    }

    [Fact]
    public async Task ListLanDevices_PopulatedStore_ReturnsDevicesInLastSeenDescendingOrder() {
        _lanDeviceStore.Seed(MakeDevice("aa:aa:aa:aa:aa:01", "192.168.1.10", lastSeen: FixedTimestamp.AddSeconds(-30)));
        _lanDeviceStore.Seed(MakeDevice("aa:aa:aa:aa:aa:02", "192.168.1.11", lastSeen: FixedTimestamp));
        _lanDeviceStore.Seed(MakeDevice("aa:aa:aa:aa:aa:03", "192.168.1.12", lastSeen: FixedTimestamp.AddSeconds(-60)));

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var response = await _service.ListLanDevices(new Local.ListLanDevicesRequest(), context);

        Assert.Equal(3, response.Devices.Count);
        // Most-recently-seen first.
        Assert.Equal("aa:aa:aa:aa:aa:02", response.Devices[0].Mac);
        Assert.Equal("aa:aa:aa:aa:aa:01", response.Devices[1].Mac);
        Assert.Equal("aa:aa:aa:aa:aa:03", response.Devices[2].Mac);
    }

    [Fact]
    public async Task ListLanDevices_SeenSinceFilter_DropsOlderRows() {
        _lanDeviceStore.Seed(MakeDevice("aa:aa:aa:aa:aa:01", "192.168.1.10", lastSeen: FixedTimestamp.AddDays(-2)));
        _lanDeviceStore.Seed(MakeDevice("aa:aa:aa:aa:aa:02", "192.168.1.11", lastSeen: FixedTimestamp));

        var cutoff = FixedTimestamp.AddHours(-1);
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var response = await _service.ListLanDevices(
            new Local.ListLanDevicesRequest { SeenSinceUnixNs = cutoff.ToUnixTimeMilliseconds() * 1_000_000L },
            context);

        var only = Assert.Single(response.Devices);
        Assert.Equal("aa:aa:aa:aa:aa:02", only.Mac);
    }

    [Fact]
    public async Task ListLanDevices_LimitZero_UsesServerDefaultOf200() {
        // Server default is 200 â€” seed 250 and confirm only the top-200 (newest)
        // come back. Test guards against the default silently widening.
        for (var i = 0; i < 250; i++) {
            _lanDeviceStore.Seed(MakeDevice(
                $"aa:aa:aa:aa:{i:x2}:{(i >> 8):x2}",
                $"192.168.{i / 256}.{i % 256}",
                lastSeen: FixedTimestamp.AddSeconds(-i)));
        }

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var response = await _service.ListLanDevices(
            new Local.ListLanDevicesRequest { Limit = 0 }, context);

        Assert.Equal(200, response.Devices.Count);
    }

    [Fact]
    public async Task ListLanDevices_LimitAboveServerCap_ClampsToMaximum() {
        for (var i = 0; i < 1500; i++) {
            _lanDeviceStore.Seed(MakeDevice(
                $"bb:bb:bb:bb:{i:x2}:{(i >> 8):x2}",
                $"10.0.{i / 256}.{i % 256}",
                lastSeen: FixedTimestamp.AddSeconds(-i)));
        }

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var response = await _service.ListLanDevices(
            new Local.ListLanDevicesRequest { Limit = 5000 }, context);

        Assert.Equal(1000, response.Devices.Count);
    }

    [Fact]
    public async Task ListLanDevices_ExplicitLimit_BelowCap_Honored() {
        _lanDeviceStore.Seed(MakeDevice("aa:aa:aa:aa:aa:01", "192.168.1.10", lastSeen: FixedTimestamp.AddSeconds(-10)));
        _lanDeviceStore.Seed(MakeDevice("aa:aa:aa:aa:aa:02", "192.168.1.11", lastSeen: FixedTimestamp));
        _lanDeviceStore.Seed(MakeDevice("aa:aa:aa:aa:aa:03", "192.168.1.12", lastSeen: FixedTimestamp.AddSeconds(-20)));

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var response = await _service.ListLanDevices(
            new Local.ListLanDevicesRequest { Limit = 2 }, context);

        Assert.Equal(2, response.Devices.Count);
        // Newest two â€” order is LastSeen DESC.
        Assert.Equal("aa:aa:aa:aa:aa:02", response.Devices[0].Mac);
        Assert.Equal("aa:aa:aa:aa:aa:01", response.Devices[1].Mac);
    }

    [Fact]
    public async Task ListLanDevices_NegativeLimit_ThrowsInvalidArgument() {
        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => _service.ListLanDevices(new Local.ListLanDevicesRequest { Limit = -1 }, context));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task ListLanDevices_VendorAndHostnameNull_MappedToEmptyStringOnWire() {
        _lanDeviceStore.Seed(new LanDevice(
            Mac: "aa:aa:aa:aa:aa:99",
            Ip: "192.168.1.99",
            Vendor: null,
            Hostname: null,
            FirstSeen: FixedTimestamp.AddDays(-1),
            LastSeen: FixedTimestamp,
            Label: null));

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var response = await _service.ListLanDevices(new Local.ListLanDevicesRequest(), context);

        var only = Assert.Single(response.Devices);
        Assert.Equal("", only.Vendor);
        Assert.Equal("", only.Hostname);
    }

    [Fact]
    public async Task ListLanDevices_StoreThrows_MapsToInternalRpcError() {
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
        var ex = await Assert.ThrowsAsync<RpcException>(
            () => service.ListLanDevices(new Local.ListLanDevicesRequest(), context));
        Assert.Equal(StatusCode.Internal, ex.StatusCode);
    }

    // --- Helpers ---

    private static LanDevice MakeDevice(string mac, string ip, DateTimeOffset lastSeen) =>
        new(Mac: mac, Ip: ip, Vendor: "TestVendor", Hostname: "test-host",
            FirstSeen: lastSeen.AddDays(-1), LastSeen: lastSeen, Label: null);

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
}
