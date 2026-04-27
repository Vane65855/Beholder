using System;

namespace Beholder.Daemon;

/// <summary>
/// Holds the runtime-mutable enforcement flag for the firewall master toggle.
/// Separated from <see cref="FirewallOptions"/> because <c>IOptionsMonitor</c>
/// reads from <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>,
/// and rewriting <c>appsettings.json</c> from inside an RPC handler to flip
/// a single bool would be both invasive and surprising. Instead, the daemon
/// seeds this service from <see cref="FirewallOptions.EnableEnforcement"/>
/// at startup, and the <c>SetFirewallEnabled</c> RPC mutates it directly.
/// </summary>
internal interface IFirewallEnforcementState {
    /// <summary>
    /// Current enforcement state. <c>true</c> means rule mutations propagate
    /// to the OS firewall; <c>false</c> means they're persisted to SQLite
    /// only. Initial value comes from <see cref="FirewallOptions.EnableEnforcement"/>.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Atomically updates <see cref="Enabled"/> and raises <see cref="StateChanged"/>
    /// when the value actually changes. Returns <c>true</c> if the value
    /// changed, <c>false</c> if <paramref name="enabled"/> matched the current
    /// state (in which case no event fires). Idempotent on no-op transitions.
    /// </summary>
    bool SetEnabled(bool enabled);

    /// <summary>
    /// Raised after <see cref="Enabled"/> transitions. The argument is the
    /// new state. Subscribers (notably <see cref="Pipeline.FirewallEnforcementService"/>)
    /// run synchronously from <see cref="SetEnabled"/>; long-running work
    /// must be dispatched off-thread by the subscriber.
    /// </summary>
    event Action<bool>? StateChanged;
}
