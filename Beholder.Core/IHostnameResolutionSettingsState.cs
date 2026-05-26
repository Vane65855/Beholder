namespace Beholder.Core;

/// <summary>
/// Runtime-mutable state for the Hostname Resolution section of Settings —
/// three boolean toggles controlling how the daemon resolves IPs to names.
/// Same shape as <see cref="IRecordingSettingsState"/>: read-only getters,
/// one atomic <c>SetSettings</c> for the whole section, and a
/// <see cref="StateChanged"/> event.
/// </summary>
/// <remarks>
/// <para>Only <see cref="EnableReverseDnsFallback"/> takes effect immediately
/// when toggled (the <c>ReverseDnsFallbackCache.Resolve</c> consumer reads it
/// per call). <see cref="EnablePreload"/> and <see cref="EnableSniCapture"/>
/// take effect only on the next daemon start — their consumers
/// (<c>EtwDnsCache.PreloadFromWindowsDnsCache</c>,
/// <c>PktmonSniSource.StartAsync</c>) snapshot the value at startup and would
/// need ETW session lifecycle refactoring to live-toggle. The Settings UI
/// renders a "(takes effect on next daemon start)" caption next to those two
/// toggles to make the timing honest.</para>
/// <para>Initial state seeded from <c>DnsOptions</c> + <c>SniOptions</c> at
/// construction; persisted overrides applied by the
/// <c>SettingsOverridesService</c> hosted service at startup.</para>
/// </remarks>
public interface IHostnameResolutionSettingsState {
    /// <summary>
    /// When true, the daemon runs <c>EtwDnsCache.PreloadFromWindowsDnsCache</c>
    /// once at <c>StartAsync</c> to seed the DNS cache from Windows's own
    /// resolver cache. Snapshot-at-startup; not live.
    /// </summary>
    bool EnablePreload { get; }

    /// <summary>
    /// When true, <c>ReverseDnsFallbackCache.Resolve</c> issues a reverse-DNS
    /// PTR query when the in-memory DNS cache misses. Read per-call; takes
    /// effect immediately when toggled.
    /// </summary>
    bool EnableReverseDnsFallback { get; }

    /// <summary>
    /// When true, the daemon runs the PktMon ETW session that extracts SNI
    /// hostnames from TLS ClientHello packets. Snapshot-at-startup; not live.
    /// </summary>
    bool EnableSniCapture { get; }

    /// <summary>
    /// Atomically updates the Hostname Resolution section. Returns true if
    /// any of the three values changed; false on a no-op set. Fires
    /// <see cref="StateChanged"/> only on real transitions.
    /// </summary>
    bool SetSettings(bool enablePreload, bool enableReverseDnsFallback, bool enableSniCapture);

    event Action<HostnameResolutionSettingsSnapshot>? StateChanged;
}

/// <summary>
/// Immutable snapshot of the Hostname Resolution section's state, passed to
/// <see cref="IHostnameResolutionSettingsState.StateChanged"/> subscribers.
/// </summary>
public sealed record HostnameResolutionSettingsSnapshot(
    bool EnablePreload,
    bool EnableReverseDnsFallback,
    bool EnableSniCapture);
