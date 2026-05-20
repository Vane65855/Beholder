using Beholder.Protocol.Local;
using Beholder.Ui.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public class LanDeviceRowTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

    private static LanDevice MakeProto(
        string mac = "aa:bb:cc:dd:ee:01",
        string ip = "192.168.1.42",
        string vendor = "Acme Inc.",
        string hostname = "test-host",
        DateTimeOffset? firstSeen = null,
        DateTimeOffset? lastSeen = null,
        string label = ""
    ) {
        var first = firstSeen ?? FixedTimestamp.AddDays(-1);
        var last = lastSeen ?? FixedTimestamp;
        return new LanDevice {
            Mac = mac,
            Ip = ip,
            Vendor = vendor,
            Hostname = hostname,
            FirstSeenUnixNs = first.ToUnixTimeMilliseconds() * 1_000_000L,
            LastSeenUnixNs = last.ToUnixTimeMilliseconds() * 1_000_000L,
            Label = label,
        };
    }

    [Fact]
    public void FromProto_AllFieldsPreserved() {
        var proto = MakeProto();
        var clock = new FakeTimeProvider(FixedTimestamp);

        var row = LanDeviceRow.FromProto(proto, clock);

        Assert.Equal("aa:bb:cc:dd:ee:01", row.Mac);
        Assert.Equal("192.168.1.42", row.Ip);
        Assert.Equal("Acme Inc.", row.Vendor);
        Assert.Equal("test-host", row.Hostname);
        Assert.Equal(FixedTimestamp.AddDays(-1), row.FirstSeen);
        Assert.Equal(FixedTimestamp, row.LastSeen);
    }

    [Fact]
    public void FromProto_EmptyHostname_DisplayNameFallsBackToIp() {
        var proto = MakeProto(hostname: "");
        var clock = new FakeTimeProvider(FixedTimestamp);

        var row = LanDeviceRow.FromProto(proto, clock);

        Assert.Equal("192.168.1.42", row.DisplayName);
    }

    [Fact]
    public void FromProto_NonEmptyHostname_DisplayNameUsesHostname() {
        var proto = MakeProto(hostname: "living-room-tv");
        var clock = new FakeTimeProvider(FixedTimestamp);

        var row = LanDeviceRow.FromProto(proto, clock);

        Assert.Equal("living-room-tv", row.DisplayName);
    }

    [Fact]
    public void FromProto_EmptyVendor_VendorLabelFallsBackToUnknownVendor() {
        var proto = MakeProto(vendor: "");
        var clock = new FakeTimeProvider(FixedTimestamp);

        var row = LanDeviceRow.FromProto(proto, clock);

        Assert.Equal("Unknown vendor", row.VendorLabel);
    }

    [Fact]
    public void FromProto_NonEmptyVendor_VendorLabelUsesVendor() {
        var proto = MakeProto(vendor: "Apple, Inc.");
        var clock = new FakeTimeProvider(FixedTimestamp);

        var row = LanDeviceRow.FromProto(proto, clock);

        Assert.Equal("Apple, Inc.", row.VendorLabel);
    }

    [Fact]
    public void FromProto_RecentLastSeen_IsStaleFalse() {
        var proto = MakeProto(lastSeen: FixedTimestamp.AddSeconds(-10));
        var clock = new FakeTimeProvider(FixedTimestamp);

        var row = LanDeviceRow.FromProto(proto, clock);

        Assert.False(row.IsStale);
        Assert.Contains("ago", row.LastSeenLabel);
    }

    [Fact]
    public void FromProto_OldLastSeen_IsStaleTrue() {
        // Older than the 10-minute (600 s) StaleThreshold.
        var proto = MakeProto(lastSeen: FixedTimestamp.AddMinutes(-20));
        var clock = new FakeTimeProvider(FixedTimestamp);

        var row = LanDeviceRow.FromProto(proto, clock);

        Assert.True(row.IsStale);
    }

    [Fact]
    public void RefreshRelativeLabels_AdvancesClock_BumpsLastSeenLabel() {
        var proto = MakeProto(lastSeen: FixedTimestamp);
        var clock = new FakeTimeProvider(FixedTimestamp);
        var row = LanDeviceRow.FromProto(proto, clock);
        var initialLabel = row.LastSeenLabel;

        // Advance the clock by 30 s — the label should reflect the new
        // elapsed time, not the original "just now" / "0s ago".
        clock.Advance(TimeSpan.FromSeconds(30));
        row.RefreshRelativeLabels(clock);

        Assert.NotEqual(initialLabel, row.LastSeenLabel);
        Assert.Contains("30s ago", row.LastSeenLabel);
    }

    [Fact]
    public void RefreshFromProto_UpdatesLastSeenInPlace() {
        var proto = MakeProto(lastSeen: FixedTimestamp.AddMinutes(-10));
        var clock = new FakeTimeProvider(FixedTimestamp);
        var row = LanDeviceRow.FromProto(proto, clock);

        var refreshedProto = MakeProto(lastSeen: FixedTimestamp);
        row.RefreshFromProto(refreshedProto, clock);

        Assert.Equal(FixedTimestamp, row.LastSeen);
        Assert.False(row.IsStale);  // refreshed within the threshold
    }

    // ---- Phase 9.5: Label + LabelOrFallback ----

    [Fact]
    public void FromProto_WithLabel_PreservesLabel() {
        var proto = MakeProto(label: "Living Room TV");
        var clock = new FakeTimeProvider(FixedTimestamp);

        var row = LanDeviceRow.FromProto(proto, clock);

        Assert.Equal("Living Room TV", row.Label);
        Assert.True(row.HasLabel);
    }

    [Fact]
    public void FromProto_EmptyLabel_LabelIsNull() {
        var proto = MakeProto(label: "");  // proto3 default — wire shape for "no label"
        var clock = new FakeTimeProvider(FixedTimestamp);

        var row = LanDeviceRow.FromProto(proto, clock);

        Assert.Null(row.Label);
        Assert.False(row.HasLabel);
    }

    [Fact]
    public void LabelOrFallback_LabelSet_UsesLabel() {
        var proto = MakeProto(hostname: "auto-host", label: "My Custom Name");
        var row = LanDeviceRow.FromProto(proto, new FakeTimeProvider(FixedTimestamp));

        Assert.Equal("My Custom Name", row.LabelOrFallback);
    }

    [Fact]
    public void LabelOrFallback_NoLabelButHostname_UsesHostname() {
        var proto = MakeProto(hostname: "auto-host", label: "");
        var row = LanDeviceRow.FromProto(proto, new FakeTimeProvider(FixedTimestamp));

        Assert.Equal("auto-host", row.LabelOrFallback);
    }

    [Fact]
    public void LabelOrFallback_NoLabelNoHostname_UsesIp() {
        var proto = MakeProto(ip: "10.0.0.5", hostname: "", label: "");
        var row = LanDeviceRow.FromProto(proto, new FakeTimeProvider(FixedTimestamp));

        Assert.Equal("10.0.0.5", row.LabelOrFallback);
    }

    [Fact]
    public void LabelChanged_RaisesPropertyChangedForLabelOrFallback() {
        var proto = MakeProto(hostname: "auto-host", label: "");
        var row = LanDeviceRow.FromProto(proto, new FakeTimeProvider(FixedTimestamp));
        var fallbackChanges = 0;
        row.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(LanDeviceRow.LabelOrFallback)) fallbackChanges++;
        };

        row.Label = "Renamed";

        Assert.True(fallbackChanges > 0);
        Assert.Equal("Renamed", row.LabelOrFallback);
    }
}
