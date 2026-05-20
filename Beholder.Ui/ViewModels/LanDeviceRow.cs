using System;
using System.Globalization;
using Beholder.Protocol.Local;
using Beholder.Ui.Converters;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// One row in the Scanner tab's master list. Joins a chain-row
/// <see cref="LanDevice"/> (delivered via <c>ListLanDevicesAsync</c> at
/// activation, or via the live <c>DaemonStreamSubscriber.LanDeviceFirstSeen</c>
/// / <c>LanDeviceMacChanged</c> events afterwards) with a relative-time
/// "Last seen 5s ago" label refreshed by the parent ViewModel's ticker.
/// </summary>
/// <remarks>
/// Identity is keyed on <see cref="Mac"/> per ADR 009. IP / vendor / hostname
/// are stable enough for v1 to treat as immutable per row — when a known MAC's
/// IP changes between scans, the parent ViewModel replaces the row rather
/// than mutating its IP in place. Only <see cref="LastSeen"/> and its derived
/// <see cref="LastSeenLabel"/> / <see cref="IsStale"/> change observably.
/// </remarks>
internal sealed partial class LanDeviceRow : ObservableObject {
    /// <summary>
    /// Threshold past which a device's status dot flips from "active" to
    /// "stale" — currently fixed at 2× the daemon's default scan interval
    /// (300 s). In v1 this is hardcoded; if the user makes scan interval
    /// configurable, the threshold should track it.
    /// </summary>
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(600);

    public string Mac { get; }
    public string Ip { get; }
    public string Vendor { get; }     // "" → display as "Unknown vendor"
    public string Hostname { get; }   // "" → display falls back to IP
    public DateTimeOffset FirstSeen { get; }
    public string FirstSeenLabel { get; }

    [ObservableProperty]
    private DateTimeOffset _lastSeen;

    [ObservableProperty]
    private string _lastSeenLabel = string.Empty;

    [ObservableProperty]
    private bool _isStale;

    /// <summary>
    /// Master-list display name. Hostname is preferred when present (Apple TVs,
    /// Linux/Avahi machines, printers); falls back to IP for devices the
    /// hostname ladder couldn't resolve (random-MAC phones, NetBIOS-disabled
    /// Windows boxes — see <c>docs/manual-tests/lan-scanner.md</c>).
    /// </summary>
    public string DisplayName => string.IsNullOrEmpty(Hostname) ? Ip : Hostname;

    /// <summary>
    /// Master-list secondary line. Vendor from the OUI lookup; falls back to
    /// a muted placeholder when the MAC's prefix isn't in the IEEE registry
    /// (typical for randomized phone MACs).
    /// </summary>
    public string VendorLabel => string.IsNullOrEmpty(Vendor) ? "Unknown vendor" : Vendor;

    private LanDeviceRow(
        string mac,
        string ip,
        string vendor,
        string hostname,
        DateTimeOffset firstSeen,
        DateTimeOffset lastSeen,
        DateTimeOffset now
    ) {
        Mac = mac;
        Ip = ip;
        Vendor = vendor;
        Hostname = hostname;
        FirstSeen = firstSeen;
        FirstSeenLabel = firstSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        _lastSeen = lastSeen;
        _lastSeenLabel = RelativeTimeAgoConverter.Format(lastSeen, now);
        _isStale = now - lastSeen > StaleThreshold;
    }

    /// <summary>
    /// Builds a row from the wire shape (used by both the
    /// <c>ListLanDevicesAsync</c> snapshot-fetch path and the live
    /// <c>LanDeviceFirstSeenEvent</c> / <c>LanDeviceMacChangedEvent</c>
    /// stream paths — all three produce <see cref="LanDevice"/> protos).
    /// </summary>
    public static LanDeviceRow FromProto(LanDevice proto, TimeProvider timeProvider) {
        ArgumentNullException.ThrowIfNull(proto);
        ArgumentNullException.ThrowIfNull(timeProvider);
        return new LanDeviceRow(
            mac: proto.Mac,
            ip: proto.Ip,
            vendor: proto.Vendor,
            hostname: proto.Hostname,
            firstSeen: DateTimeOffset.FromUnixTimeMilliseconds(proto.FirstSeenUnixNs / 1_000_000L),
            lastSeen: DateTimeOffset.FromUnixTimeMilliseconds(proto.LastSeenUnixNs / 1_000_000L),
            now: timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Re-runs the relative-time formatter against the current clock. The
    /// parent ViewModel's 1-second ticker calls this on each visible row so
    /// labels stay live without rebinding through the converter.
    /// </summary>
    public void RefreshRelativeLabels(TimeProvider timeProvider) {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var now = timeProvider.GetUtcNow();
        LastSeenLabel = RelativeTimeAgoConverter.Format(LastSeen, now);
        IsStale = now - LastSeen > StaleThreshold;
    }

    /// <summary>
    /// Refreshes the row in place from a more recent wire snapshot. Only
    /// fields that legitimately change between scans (LastSeen, optionally
    /// Hostname / Vendor if they were resolved late) are updated; identity
    /// (Mac, FirstSeen) stays put. Used by the upsert path when an existing
    /// MAC re-appears.
    /// </summary>
    public void RefreshFromProto(LanDevice proto, TimeProvider timeProvider) {
        ArgumentNullException.ThrowIfNull(proto);
        ArgumentNullException.ThrowIfNull(timeProvider);
        LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(proto.LastSeenUnixNs / 1_000_000L);
        RefreshRelativeLabels(timeProvider);
    }
}
