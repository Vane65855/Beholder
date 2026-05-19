using Beholder.Core;

namespace Beholder.Tests;

/// <summary>
/// In-memory <see cref="IFirewallRuleStore"/> for tests that need to satisfy
/// the dependency but don't exercise firewall persistence themselves. Existing
/// firewall RPC tests use the real SQLite store; this fake exists so unrelated
/// RPC tests (e.g., LAN scanner ones) can construct
/// <c>BeholderLocalService</c> without dragging in the SQLite boilerplate.
/// </summary>
internal sealed class FakeFirewallRuleStore : IFirewallRuleStore {
    private readonly Dictionary<(string Path, Direction Direction), FirewallRule> _byKey = new();
    private int _nextId = 1;

    public Task<FirewallRule> UpsertAsync(FirewallRule rule, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(rule);
        var key = (rule.ProcessPath, rule.Direction);
        var assignedId = _byKey.TryGetValue(key, out var existing) ? existing.Id : _nextId++;
        var assigned = new FirewallRule(
            id: assignedId,
            processPath: rule.ProcessPath,
            direction: rule.Direction,
            action: rule.Action,
            source: rule.Source,
            createdAt: rule.CreatedAt,
            updatedAt: rule.UpdatedAt);
        _byKey[key] = assigned;
        return Task.FromResult(assigned);
    }

    public Task<FirewallRule?> GetByProcessAndDirectionAsync(
        string processPath, Direction direction, CancellationToken cancellationToken) {
        _byKey.TryGetValue((processPath, direction), out var rule);
        return Task.FromResult<FirewallRule?>(rule);
    }

    public Task<IReadOnlyList<FirewallRule>> ListAllAsync(CancellationToken cancellationToken) {
        IReadOnlyList<FirewallRule> all = _byKey.Values.OrderBy(r => r.Id).ToList();
        return Task.FromResult(all);
    }

    public Task<bool> RemoveAsync(string processPath, Direction direction, CancellationToken cancellationToken) =>
        Task.FromResult(_byKey.Remove((processPath, direction)));
}
