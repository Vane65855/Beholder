namespace Beholder.Daemon;

/// <summary>
/// Startup defaults for firewall enforcement. Bound from the <c>"Firewall"</c>
/// section of <c>appsettings.json</c>. Read once at daemon startup to seed
/// <see cref="IFirewallEnforcementState"/>; runtime mutations go through that
/// service instead of through this options object so RPC-driven toggles
/// don't require rewriting the config file.
/// </summary>
internal sealed class FirewallOptions {
    /// <summary>
    /// Initial value for <see cref="IFirewallEnforcementState.Enabled"/>.
    /// When true (default), Beholder applies and removes Windows Firewall
    /// rules per user actions. When false, the daemon still persists rule
    /// changes to SQLite but does not push them to the OS — useful for
    /// users who want to configure rules in advance and turn enforcement
    /// on later, or for debugging without modifying live firewall state.
    /// </summary>
    public bool EnableEnforcement { get; set; } = true;
}
