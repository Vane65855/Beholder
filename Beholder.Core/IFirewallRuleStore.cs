namespace Beholder.Core;

/// <summary>
/// Persistence layer for firewall rules managed by the daemon. Distinct from
/// <see cref="IFirewallController"/>, which is the OS-level enforcement surface —
/// this interface remembers rules across restarts.
/// </summary>
public interface IFirewallRuleStore {
    /// <summary>
    /// Inserts a new rule or updates an existing one keyed by
    /// (<see cref="FirewallRule.ProcessPath"/>, <see cref="FirewallRule.Direction"/>).
    /// Returns the materialized row including the database-assigned ID.
    /// </summary>
    Task<FirewallRule> UpsertAsync(FirewallRule rule, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the rule for the given process and direction, or <c>null</c> if none exists.
    /// </summary>
    Task<FirewallRule?> GetByProcessAndDirectionAsync(
        string processPath, Direction direction, CancellationToken cancellationToken);

    /// <summary>Returns all persisted firewall rules in ID order.</summary>
    Task<IReadOnlyList<FirewallRule>> ListAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes the rule for the given process and direction. Returns <c>true</c> if a
    /// row was deleted, <c>false</c> if no matching rule existed.
    /// </summary>
    Task<bool> RemoveAsync(string processPath, Direction direction, CancellationToken cancellationToken);
}
