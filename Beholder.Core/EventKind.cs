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

    /// <summary>
    /// User toggled one or more values in the Settings tab's Recording section
    /// (Phase 13.2). Chain-audit kind only — captures the new bundle of values
    /// + the wall-clock time of the change so the audit log can reconstruct
    /// "who set what when" without the daemon having to query the
    /// <c>settings_overrides</c> table.
    /// </summary>
    RecordingSettingsChanged = 11,

    /// <summary>
    /// User toggled one or more values in the Settings tab's Hostname Resolution
    /// section (Phase 13.2). Same chain-audit semantics as
    /// <see cref="RecordingSettingsChanged"/>.
    /// </summary>
    HostnameResolutionSettingsChanged = 12,

    /// <summary>
    /// User toggled one or more values in the Settings tab's Alerts section
    /// (Phase 13.3) — the master kill-switches for the three alert detectors
    /// (<c>EnableNewProcessDetection</c>, <c>EnableHashChangeDetection</c>,
    /// <c>EnableChainIntegrityMonitor</c>). Chain-audit kind only.
    /// </summary>
    AlertSettingsChanged = 13,

    /// <summary>
    /// User toggled the Settings tab's Scanner section value (Phase 13.4) —
    /// <c>EnableHostnameResolution</c>. Chain-audit kind only.
    /// </summary>
    ScannerSettingsChanged = 14,

    /// <summary>
    /// User added a manual application-identity rule via the Settings tab's
    /// Application Identity Overrides section (Phase 13.6). The rule tells
    /// the daemon that a binary exactly one folder below the configured
    /// anchor is the same logical app across versions — suppresses
    /// <see cref="NewProcess"/> alerts on the next install. Chain-audit
    /// kind only.
    /// </summary>
    AppIdentityRuleCreated = 15,

    /// <summary>
    /// User removed a manual application-identity rule (Phase 13.6). Future
    /// version installs under the rule's anchor will fire
    /// <see cref="NewProcess"/> alerts again. Chain-audit kind only.
    /// </summary>
    AppIdentityRuleRemoved = 16,
}
