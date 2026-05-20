using Beholder.Core;
using Beholder.Daemon.Storage;

namespace Beholder.Tests;

public sealed class SqliteLanDeviceStoreTests : IDisposable {
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly string _databasePath;
    private readonly SqliteLanDeviceStore _store;

    public SqliteLanDeviceStoreTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
        _databasePath = Path.Combine(_tempDir, "beholder.db");
        new DatabaseInitializer(_databasePath, pooling: false).Initialize();
        _store = new SqliteLanDeviceStore(new ConnectionFactory(_databasePath, pooling: false));
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Constructor_NullConnectionFactory_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => new SqliteLanDeviceStore(null!));
    }

    [Fact]
    public async Task UpsertAsync_NewMac_InsertsRow() {
        var device = MakeDevice(mac: "aa:bb:cc:11:22:33", ip: "192.168.1.10");

        await _store.UpsertAsync(device, CancellationToken.None);

        var fetched = await _store.GetByMacAsync("aa:bb:cc:11:22:33", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal("aa:bb:cc:11:22:33", fetched.Mac);
        Assert.Equal("192.168.1.10", fetched.Ip);
        Assert.Equal("AcmeCorp", fetched.Vendor);
        Assert.Equal("router.lan", fetched.Hostname);
        Assert.Equal(BaseTime, fetched.FirstSeen);
        Assert.Equal(BaseTime, fetched.LastSeen);
    }

    [Fact]
    public async Task UpsertAsync_ExistingMac_UpdatesFieldsAndPreservesFirstSeen() {
        var initial = MakeDevice(
            mac: "aa:bb:cc:11:22:33",
            ip: "192.168.1.10",
            vendor: "AcmeCorp",
            hostname: "old-name.lan",
            firstSeen: BaseTime,
            lastSeen: BaseTime);
        await _store.UpsertAsync(initial, CancellationToken.None);

        var later = BaseTime.AddHours(3);
        var updated = MakeDevice(
            mac: "aa:bb:cc:11:22:33",
            ip: "192.168.1.99",
            vendor: "AcmeCorp Updated",
            hostname: "new-name.lan",
            firstSeen: later,
            lastSeen: later);
        await _store.UpsertAsync(updated, CancellationToken.None);

        var fetched = await _store.GetByMacAsync("aa:bb:cc:11:22:33", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal("192.168.1.99", fetched.Ip);
        Assert.Equal("AcmeCorp Updated", fetched.Vendor);
        Assert.Equal("new-name.lan", fetched.Hostname);
        Assert.Equal(BaseTime, fetched.FirstSeen);    // preserved
        Assert.Equal(later, fetched.LastSeen);
    }

    [Fact]
    public async Task GetByMacAsync_KnownMac_ReturnsDevice() {
        await _store.UpsertAsync(MakeDevice(mac: "aa:bb:cc:11:22:33"), CancellationToken.None);

        var fetched = await _store.GetByMacAsync("aa:bb:cc:11:22:33", CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal("aa:bb:cc:11:22:33", fetched.Mac);
    }

    [Fact]
    public async Task GetByMacAsync_UnknownMac_ReturnsNull() {
        var fetched = await _store.GetByMacAsync("ff:ff:ff:ff:ff:ff", CancellationToken.None);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task GetByIpAsync_KnownIp_ReturnsDevice() {
        await _store.UpsertAsync(MakeDevice(mac: "aa:bb:cc:11:22:33", ip: "10.0.0.42"), CancellationToken.None);

        var fetched = await _store.GetByIpAsync("10.0.0.42", CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal("aa:bb:cc:11:22:33", fetched.Mac);
        Assert.Equal("10.0.0.42", fetched.Ip);
    }

    [Fact]
    public async Task GetByIpAsync_UnknownIp_ReturnsNull() {
        var fetched = await _store.GetByIpAsync("10.0.0.99", CancellationToken.None);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task GetByIpAsync_TwoDevicesShareIp_ReturnsOneDevice() {
        // Locks in the design decision that IP uniqueness is NOT enforced.
        // The scanner in 9.2 uses GetByIpAsync + explicit MAC compare to detect
        // MAC changes; this test guards that two rows with the same IP don't
        // throw and the method returns one of them.
        await _store.UpsertAsync(MakeDevice(mac: "aa:aa:aa:aa:aa:aa", ip: "10.0.0.42"), CancellationToken.None);
        await _store.UpsertAsync(MakeDevice(mac: "bb:bb:bb:bb:bb:bb", ip: "10.0.0.42"), CancellationToken.None);

        var fetched = await _store.GetByIpAsync("10.0.0.42", CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal("10.0.0.42", fetched.Ip);
        Assert.Contains(fetched.Mac, new[] { "aa:aa:aa:aa:aa:aa", "bb:bb:bb:bb:bb:bb" });
    }

    [Fact]
    public async Task ListAsync_NoFilters_ReturnsAllOrderedByLastSeenDesc() {
        await _store.UpsertAsync(MakeDevice(
            mac: "aa:aa:aa:aa:aa:aa", lastSeen: BaseTime), CancellationToken.None);
        await _store.UpsertAsync(MakeDevice(
            mac: "bb:bb:bb:bb:bb:bb", lastSeen: BaseTime.AddMinutes(10)), CancellationToken.None);
        await _store.UpsertAsync(MakeDevice(
            mac: "cc:cc:cc:cc:cc:cc", lastSeen: BaseTime.AddMinutes(5)), CancellationToken.None);

        var devices = await _store.ListAsync(new LanDeviceQuery(SeenSince: null, Limit: 0), CancellationToken.None);

        Assert.Equal(3, devices.Count);
        Assert.Equal("bb:bb:bb:bb:bb:bb", devices[0].Mac);
        Assert.Equal("cc:cc:cc:cc:cc:cc", devices[1].Mac);
        Assert.Equal("aa:aa:aa:aa:aa:aa", devices[2].Mac);
    }

    [Fact]
    public async Task ListAsync_WithSeenSince_FiltersOlderRows() {
        await _store.UpsertAsync(MakeDevice(
            mac: "aa:aa:aa:aa:aa:aa", lastSeen: BaseTime), CancellationToken.None);
        await _store.UpsertAsync(MakeDevice(
            mac: "bb:bb:bb:bb:bb:bb", lastSeen: BaseTime.AddHours(2)), CancellationToken.None);

        var threshold = BaseTime.AddHours(1);
        var devices = await _store.ListAsync(
            new LanDeviceQuery(SeenSince: threshold, Limit: 0), CancellationToken.None);

        Assert.Single(devices);
        Assert.Equal("bb:bb:bb:bb:bb:bb", devices[0].Mac);
    }

    [Fact]
    public async Task ListAsync_WithLimit_TruncatesToTopN() {
        for (var i = 0; i < 5; i++) {
            await _store.UpsertAsync(MakeDevice(
                mac: $"aa:aa:aa:aa:aa:{i:X2}", lastSeen: BaseTime.AddMinutes(i)), CancellationToken.None);
        }

        var devices = await _store.ListAsync(new LanDeviceQuery(SeenSince: null, Limit: 2), CancellationToken.None);

        Assert.Equal(2, devices.Count);
        // Top 2 by last_seen DESC means indices 4 and 3.
        Assert.Equal("aa:aa:aa:aa:aa:04", devices[0].Mac);
        Assert.Equal("aa:aa:aa:aa:aa:03", devices[1].Mac);
    }

    [Fact]
    public async Task ListAsync_EmptyTable_ReturnsEmptyList() {
        var devices = await _store.ListAsync(new LanDeviceQuery(SeenSince: null, Limit: 0), CancellationToken.None);

        Assert.NotNull(devices);
        Assert.Empty(devices);
    }

    [Fact]
    public async Task UpsertAsync_NullVendorAndHostname_RoundTripsAsNull() {
        var device = MakeDevice(mac: "aa:bb:cc:11:22:33", vendor: null, hostname: null);

        await _store.UpsertAsync(device, CancellationToken.None);

        var fetched = await _store.GetByMacAsync("aa:bb:cc:11:22:33", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Null(fetched.Vendor);
        Assert.Null(fetched.Hostname);
    }

    // ---- Phase 9.5: label round-trip + SetLabelAsync ----

    [Fact]
    public async Task UpsertAsync_DeviceWithLabel_RoundTripsLabel() {
        var device = MakeDevice(mac: "aa:bb:cc:11:22:34", label: "Living Room TV");

        await _store.UpsertAsync(device, CancellationToken.None);

        var fetched = await _store.GetByMacAsync("aa:bb:cc:11:22:34", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal("Living Room TV", fetched.Label);
    }

    [Fact]
    public async Task UpsertAsync_KnownMacReObservedWithNullLabel_PreservesExistingLabel() {
        // Critical invariant: scanner re-observations carry null for the
        // label (the scanner has no notion of user labels). The ON CONFLICT
        // SET clause deliberately omits the label column so the user's
        // prior label persists across re-observations.
        await _store.UpsertAsync(MakeDevice(mac: "aa:bb:cc:11:22:35", label: "Kitchen TV"), CancellationToken.None);

        await _store.UpsertAsync(MakeDevice(
            mac: "aa:bb:cc:11:22:35",
            label: null,
            lastSeen: BaseTime.AddHours(1)), CancellationToken.None);

        var fetched = await _store.GetByMacAsync("aa:bb:cc:11:22:35", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal("Kitchen TV", fetched.Label);  // preserved
        Assert.Equal(BaseTime.AddHours(1), fetched.LastSeen);  // refreshed
    }

    [Fact]
    public async Task SetLabelAsync_NewLabel_PersistsAndRoundTrips() {
        await _store.UpsertAsync(MakeDevice(mac: "aa:bb:cc:11:22:36"), CancellationToken.None);

        await _store.SetLabelAsync("aa:bb:cc:11:22:36", "Office Printer", CancellationToken.None);

        var fetched = await _store.GetByMacAsync("aa:bb:cc:11:22:36", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal("Office Printer", fetched.Label);
    }

    [Fact]
    public async Task SetLabelAsync_NullLabel_ClearsExistingLabel() {
        await _store.UpsertAsync(MakeDevice(mac: "aa:bb:cc:11:22:37", label: "Old Name"), CancellationToken.None);

        await _store.SetLabelAsync("aa:bb:cc:11:22:37", null, CancellationToken.None);

        var fetched = await _store.GetByMacAsync("aa:bb:cc:11:22:37", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Null(fetched.Label);
    }

    [Fact]
    public async Task SetLabelAsync_WhitespaceLabel_ClearsExistingLabel() {
        // The store normalises whitespace-only to null per the interface
        // contract — UI's empty TextBox round-trips cleanly to "no label".
        await _store.UpsertAsync(MakeDevice(mac: "aa:bb:cc:11:22:38", label: "Stale"), CancellationToken.None);

        await _store.SetLabelAsync("aa:bb:cc:11:22:38", "   ", CancellationToken.None);

        var fetched = await _store.GetByMacAsync("aa:bb:cc:11:22:38", CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Null(fetched.Label);
    }

    [Fact]
    public async Task SetLabelAsync_UnknownMac_NoOpDoesNotThrow() {
        // Per the interface contract: SetLabelAsync against a MAC that's
        // not in the store is a silent no-op. Callers can detect this via
        // a follow-up GetByMacAsync.
        await _store.SetLabelAsync("99:99:99:99:99:99", "ghost", CancellationToken.None);

        var fetched = await _store.GetByMacAsync("99:99:99:99:99:99", CancellationToken.None);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task ListAsync_WithLabels_LabelsReturned() {
        await _store.UpsertAsync(MakeDevice(mac: "aa:bb:cc:11:22:40", label: "Device A"), CancellationToken.None);
        await _store.UpsertAsync(MakeDevice(mac: "aa:bb:cc:11:22:41", label: null), CancellationToken.None);

        var devices = await _store.ListAsync(new LanDeviceQuery(SeenSince: null, Limit: 0), CancellationToken.None);

        Assert.Equal(2, devices.Count);
        var a = devices.Single(d => d.Mac == "aa:bb:cc:11:22:40");
        var b = devices.Single(d => d.Mac == "aa:bb:cc:11:22:41");
        Assert.Equal("Device A", a.Label);
        Assert.Null(b.Label);
    }

    private static LanDevice MakeDevice(
        string mac = "aa:bb:cc:11:22:33",
        string ip = "192.168.1.10",
        string? vendor = "AcmeCorp",
        string? hostname = "router.lan",
        DateTimeOffset? firstSeen = null,
        DateTimeOffset? lastSeen = null,
        string? label = null
    ) => new LanDevice(
        Mac: mac,
        Ip: ip,
        Vendor: vendor,
        Hostname: hostname,
        FirstSeen: firstSeen ?? BaseTime,
        LastSeen: lastSeen ?? BaseTime,
        Label: label
    );
}
