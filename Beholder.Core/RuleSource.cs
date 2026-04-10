namespace Beholder.Core;

/// <summary>
/// Origin of a Beholder-managed firewall rule. Recorded so the daemon can distinguish
/// rules the user added by hand from rules created by built-in defaults or pushed down
/// from a remote aggregator, and so the audit trail is meaningful.
/// </summary>
public enum RuleSource {
    /// <summary>The user explicitly created the rule from the local UI.</summary>
    Manual,

    /// <summary>The daemon installed the rule as part of its built-in defaults.</summary>
    Default,

    /// <summary>The rule was pushed down by a remote aggregator over the uplink.</summary>
    Remote,
}
