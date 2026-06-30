using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

internal sealed class FakeFirewallController : IFirewallController {
    public List<FirewallRule> AddedRules { get; } = new();
    public List<(string ProcessPath, Direction Direction)> RemovedRules { get; } = new();

    /// <summary>The Beholder rules the OS firewall currently holds (what <see cref="ListRulesAsync"/> returns).</summary>
    public List<FirewallRule> OsRules { get; } = new();

    public Exception? AddRuleException { get; set; }
    public Exception? RemoveRuleException { get; set; }
    public Exception? ListRulesException { get; set; }

    public Task AddRuleAsync(FirewallRule rule, CancellationToken cancellationToken) {
        if (AddRuleException is not null) throw AddRuleException;
        AddedRules.Add(rule);
        return Task.CompletedTask;
    }

    public Task RemoveRuleAsync(string processPath, Direction direction, CancellationToken cancellationToken) {
        if (RemoveRuleException is not null) throw RemoveRuleException;
        RemovedRules.Add((processPath, direction));
        return Task.CompletedTask;
    }

    /// <summary>Number of times <see cref="ListRulesAsync"/> was called — one per reconciliation pass.</summary>
    public int ListCallCount { get; private set; }

    public Task<IReadOnlyList<FirewallRule>> ListRulesAsync(CancellationToken cancellationToken) {
        ListCallCount++;
        if (ListRulesException is not null) throw ListRulesException;
        return Task.FromResult<IReadOnlyList<FirewallRule>>(OsRules.ToList());
    }
}
