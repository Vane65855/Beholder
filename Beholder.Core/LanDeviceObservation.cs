namespace Beholder.Core;

/// <summary>
/// One device observed during an <see cref="ILanDeviceProbe.ScanAsync"/> call.
/// Vendor enrichment via <see cref="IOuiVendorLookup"/> is performed by the
/// scheduler (not the probe) — keeping the probe layer focused on what's on
/// the wire, per SRP.
/// </summary>
/// <param name="Mac">Canonical lowercase hex with colons, e.g. <c>"aa:bb:cc:dd:ee:ff"</c>.</param>
/// <param name="Ip">Dotted-decimal IPv4 string the device responded from.</param>
/// <param name="Hostname">
/// Populated when the probe's hostname-resolution sub-layers (mDNS / NetBIOS,
/// shipped in Phase 9.2.5) succeed; <see langword="null"/> otherwise. Always
/// null in the Phase 9.2 ARP-only probe.
/// </param>
/// <param name="ObservedAt">Wall-clock time (UTC) the observation was recorded.</param>
public sealed record LanDeviceObservation(
    string Mac,
    string Ip,
    string? Hostname,
    DateTimeOffset ObservedAt);
