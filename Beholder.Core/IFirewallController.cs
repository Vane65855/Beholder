namespace Beholder.Core;

/// <summary>
/// Platform-specific firewall rule manager. Implementations translate Beholder rules
/// into the native firewall API (WFP/INetFwPolicy2 on Windows, nftables on Linux) and
/// expose only the rules Beholder itself manages.
/// </summary>
public interface IFirewallController {
    /// <summary>
    /// Returns every firewall rule currently managed by Beholder. OS-level rules
    /// created by other software are deliberately excluded.
    /// </summary>
    Task<IReadOnlyList<FirewallRule>> ListRulesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates or updates a rule in the OS firewall. If a Beholder-managed rule for
    /// the same (<see cref="FirewallRule.ProcessPath"/>,
    /// <see cref="FirewallRule.Direction"/>) pair already exists, its action is
    /// overwritten in place.
    /// </summary>
    Task AddRuleAsync(FirewallRule rule, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a Beholder-managed rule. Idempotent: returns successfully even if no
    /// matching rule exists.
    /// </summary>
    Task RemoveRuleAsync(string processPath, Direction direction, CancellationToken cancellationToken);
}
