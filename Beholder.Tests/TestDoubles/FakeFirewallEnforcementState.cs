using Beholder.Daemon;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IFirewallEnforcementState"/>. Mirrors the
/// production class's contract (idempotent <c>SetEnabled</c>, event raised
/// only on real transitions) without the production lock — tests are
/// single-threaded.
/// </summary>
internal sealed class FakeFirewallEnforcementState : IFirewallEnforcementState {
    public FakeFirewallEnforcementState(bool initialEnabled = true) {
        Enabled = initialEnabled;
    }

    public bool Enabled { get; private set; }

    public bool SetEnabled(bool enabled) {
        if (Enabled == enabled) return false;
        Enabled = enabled;
        StateChanged?.Invoke(enabled);
        return true;
    }

    public event Action<bool>? StateChanged;
}
