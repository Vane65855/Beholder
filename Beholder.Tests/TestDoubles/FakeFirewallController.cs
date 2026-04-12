using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

internal sealed class FakeFirewallController : IFirewallController {
    public List<FirewallRule> AddedRules { get; } = new();
    public List<(string ProcessPath, Direction Direction)> RemovedRules { get; } = new();
    public Exception? AddRuleException { get; set; }
    public Exception? RemoveRuleException { get; set; }

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

    public Task<IReadOnlyList<FirewallRule>> ListRulesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<FirewallRule>>(Array.Empty<FirewallRule>());
}
