namespace Beholder.Core;

/// <summary>
/// A device observed on the local subnet by the Phase 9 LAN scanner. Identity is
/// keyed on <see cref="Mac"/> per ADR 009 — IP is mutable (DHCP lease renewals),
/// the MAC is the durable layer-2 identifier. The vendor and hostname columns are
/// nullable because the OUI table may not cover every prefix and the
/// mDNS / NetBIOS / reverse-DNS hostname-resolution ladder may fail for some
/// devices.
/// </summary>
public sealed record LanDevice(
    string Mac,
    string Ip,
    string? Vendor,
    string? Hostname,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);
