namespace Beholder.Core;

/// <summary>
/// A device observed on the local subnet by the Phase 9 LAN scanner. Identity is
/// keyed on <see cref="Mac"/> per ADR 009 — IP is mutable (DHCP lease renewals),
/// the MAC is the durable layer-2 identifier. The <see cref="Vendor"/> and
/// <see cref="Hostname"/> columns are nullable because the OUI table may not
/// cover every prefix and the mDNS / NetBIOS / reverse-DNS hostname-resolution
/// ladder may fail for some devices. <see cref="Label"/> (Phase 9.5) is a
/// user-supplied cosmetic display name that overrides auto-detected hostnames
/// in the UI; it is NOT chain-audited (cosmetic UI state, same category as
/// Alert read-state) and is preserved across scanner re-observations of the
/// same MAC.
/// </summary>
public sealed record LanDevice(
    string Mac,
    string Ip,
    string? Vendor,
    string? Hostname,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string? Label);
