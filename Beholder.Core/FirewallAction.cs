namespace Beholder.Core;

/// <summary>
/// Action a firewall rule applies to a matched flow.
/// </summary>
public enum FirewallAction {
    /// <summary>Permit the matched flow.</summary>
    Allow,

    /// <summary>Drop the matched flow.</summary>
    Block,
}
