using System;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon;

/// <summary>
/// Default <see cref="IFirewallEnforcementState"/> implementation. Reads its
/// initial value from <see cref="FirewallOptions"/> and exposes a thread-safe
/// transition via a lock. Registered as a singleton so all collaborators see
/// the same flag instance.
/// </summary>
internal sealed class FirewallEnforcementState : IFirewallEnforcementState {
    private readonly object _gate = new();
    private bool _enabled;

    public FirewallEnforcementState(IOptions<FirewallOptions> options) {
        ArgumentNullException.ThrowIfNull(options);
        _enabled = options.Value.EnableEnforcement;
    }

    public bool Enabled {
        get { lock (_gate) return _enabled; }
    }

    public bool SetEnabled(bool enabled) {
        bool changed;
        lock (_gate) {
            changed = _enabled != enabled;
            _enabled = enabled;
        }
        // Fire outside the lock to avoid re-entrancy deadlocks if a subscriber
        // calls back into Enabled or SetEnabled during dispatch.
        if (changed) StateChanged?.Invoke(enabled);
        return changed;
    }

    public event Action<bool>? StateChanged;
}
