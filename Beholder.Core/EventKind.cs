namespace Beholder.Core;

/// <summary>
/// Categorizes entries appended to the chain-hashed event log. Every mutable system event
/// stored in the daemon's SQLite database carries one of these values.
/// </summary>
public enum EventKind {
    /// <summary>Reserved default. Indicates an unrecognized or uninitialized value.</summary>
    Unknown = 0,

    /// <summary>Periodic per-process byte counter delta.</summary>
    Counter = 1,

    /// <summary>A binary path accessed the network for the first time.</summary>
    NewProcess = 2,

    /// <summary>A tracked binary's SHA-256 differs from the previously stored value.</summary>
    HashChanged = 3,

    /// <summary>Hash chain verification detected a mismatch or gap.</summary>
    ChainError = 4,

    /// <summary>A new firewall rule was added to the active rule set.</summary>
    FirewallRuleCreated = 5,

    /// <summary>An existing firewall rule was modified.</summary>
    FirewallRuleChanged = 6,

    /// <summary>An existing firewall rule was removed from the active rule set.</summary>
    FirewallRuleRemoved = 7,

    /// <summary>The Beholder firewall enforcement master toggle was flipped on or off.</summary>
    FirewallEnforcementToggled = 8,

    /// <summary>
    /// A new LAN device was discovered for the first time (Phase 9.2; see ADR 009).
    /// Chain-audit kind only — NOT an alert (ADR 002 preserved). Surfaced in the
    /// Scanner tab's activity strip when 9.4 ships.
    /// </summary>
    LanDeviceFirstSeen = 9,

    /// <summary>
    /// A LAN IP previously associated with one MAC is now responding from a different
    /// MAC (Phase 9.2; see ADR 009). Most commonly a DHCP reassignment between two
    /// devices; in adversarial settings a potential ARP-spoof signal. Chain-audit
    /// kind only.
    /// </summary>
    LanDeviceMacChanged = 10,
}
