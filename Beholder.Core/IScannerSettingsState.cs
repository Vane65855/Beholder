namespace Beholder.Core;

/// <summary>
/// Runtime-mutable state for the Scanner section of Settings — the daemon's
/// LAN-device-discovery option. Same shape as the other Settings state
/// singletons (Phase 13.2 / 13.3 precedents): read-only getter, atomic
/// <see cref="SetSettings"/>, <see cref="StateChanged"/> event.
/// </summary>
/// <remarks>
/// <para><see cref="EnableHostnameResolution"/> is snapshot-at-startup. The
/// daemon's DI factory for <c>ILanDeviceProbe</c> reads the value once when
/// the probe is constructed and bakes the hostname-resolution sub-probes into
/// (or out of) the resulting <c>WindowsLanDeviceProbe</c>. Toggling at
/// runtime persists the new value but it only takes effect on the next
/// daemon start — the UI renders a "(takes effect on next daemon start)"
/// caption beside the pill to make the timing honest, mirroring the
/// Phase 13.2 precedent for <c>EnablePreload</c> and <c>EnableSniCapture</c>.
/// Live toggling would require refactoring <c>WindowsLanDeviceProbe.ScanAsync</c>
/// to consult this state per scan and conditionally invoke the probes — a
/// moderate platform-project refactor declined in 13.4 for the same reason
/// 13.2 declined the ETW session lifecycle refactor.</para>
/// <para>Initial value seeded from <c>IOptions&lt;ScannerOptions&gt;</c> at
/// construction; persisted overrides applied by the
/// <c>SettingsOverridesService</c> hosted service at startup.</para>
/// <para>The <c>ScanIntervalSeconds</c> numeric tuning knob is NOT exposed
/// here — it stays JSON-only matching the Phase 13.2 precedent for advanced
/// tuning (queue capacities, buffer sizes).</para>
/// </remarks>
public interface IScannerSettingsState {
    /// <summary>
    /// When true, the LAN scanner runs mDNS service-discovery + the per-IP
    /// hostname-resolution ladder (mDNS-PTR, NetBIOS, router-DNS) after
    /// ARP discovery. Set false to limit the scanner to passive ARP-cache
    /// reads + active ARP probes only (hostnames stay NULL on every
    /// device).
    /// </summary>
    bool EnableHostnameResolution { get; }

    /// <summary>
    /// Atomically updates the Scanner section's settings. Returns true if
    /// the value changed; false on a no-op set. Fires
    /// <see cref="StateChanged"/> only on a real transition.
    /// </summary>
    bool SetSettings(bool enableHostnameResolution);

    event Action<ScannerSettingsSnapshot>? StateChanged;
}

/// <summary>
/// Immutable snapshot of the Scanner section's state, passed to
/// <see cref="IScannerSettingsState.StateChanged"/> subscribers.
/// </summary>
public sealed record ScannerSettingsSnapshot(bool EnableHostnameResolution);
