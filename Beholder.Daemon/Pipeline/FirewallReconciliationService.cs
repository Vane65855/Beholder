using Beholder.Core;
using Beholder.Daemon.Storage;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// On daemon start, reconciles the OS firewall against the SQLite rule store
/// (the source of truth) so the two cannot drift silently — a Beholder rule
/// deleted in <c>wf.msc</c>, an action hand-edited there, or an orphan rule left
/// by a crash. When enforcement is on the OS should mirror the store exactly;
/// when off the OS should carry no Beholder rules (the store copies are kept so
/// the master toggle can re-apply them). Every drift it corrects is appended to
/// the hash chain — drift is itself a security signal — while a startup that
/// finds everything already in sync stays silent.
/// </summary>
/// <remarks>
/// Runs once, awaited in <see cref="StartAsync"/> (the rule set is small and the
/// controller serialises its own COM calls). Any failure is logged and
/// swallowed: reconciliation is best-effort hardening and must never abort
/// daemon startup. Registered before <see cref="FirewallEnforcementService"/> so
/// the OS is reconciled before the enforcement toggle is armed.
/// </remarks>
internal sealed class FirewallReconciliationService : IHostedService {
    private readonly IFirewallRuleStore _ruleStore;
    private readonly IFirewallController _controller;
    private readonly IFirewallEnforcementState _enforcement;
    private readonly IEventStore _eventStore;
    private readonly ILogger<FirewallReconciliationService> _logger;

    public FirewallReconciliationService(
        IFirewallRuleStore ruleStore,
        IFirewallController controller,
        IFirewallEnforcementState enforcement,
        IEventStore eventStore,
        ILogger<FirewallReconciliationService> logger
    ) {
        ArgumentNullException.ThrowIfNull(ruleStore);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(enforcement);
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(logger);
        _ruleStore = ruleStore;
        _controller = controller;
        _enforcement = enforcement;
        _eventStore = eventStore;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken) {
        try {
            await ReconcileAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex, "Firewall reconciliation failed; continuing startup");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Test seam: the reconciliation pass itself, without the <see cref="StartAsync"/>
    /// guard. Makes the OS Beholder-rule set match the store (or empty, when
    /// enforcement is off), chain-logging only the rules it actually changes.
    /// </summary>
    internal async Task ReconcileAsync(CancellationToken cancellationToken) {
        var dbRules = await _ruleStore.ListAllAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<FirewallRule> osRules;
        try {
            osRules = await _controller.ListRulesAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex, "Could not enumerate OS firewall rules; skipping reconciliation");
            return;
        }

        // When enforcement is off the OS should hold no Beholder rules at all.
        var expected = new Dictionary<(string, Direction), FirewallRule>();
        if (_enforcement.Enabled)
            foreach (var rule in dbRules) expected[(rule.ProcessPath, rule.Direction)] = rule;

        var osByKey = new Dictionary<(string, Direction), FirewallRule>();
        foreach (var rule in osRules) osByKey[(rule.ProcessPath, rule.Direction)] = rule;

        var added = 0;
        var changed = 0;
        var removed = 0;

        // Expected rules that are missing from the OS, or present with a stale action.
        foreach (var rule in expected.Values) {
            cancellationToken.ThrowIfCancellationRequested();
            osByKey.TryGetValue((rule.ProcessPath, rule.Direction), out var osRule);
            if (osRule is not null && osRule.Action == rule.Action) continue;

            var actionDrift = osRule is not null;
            if (!await TryApplyAsync(
                    () => _controller.AddRuleAsync(rule, cancellationToken),
                    "re-apply", rule.ProcessPath, rule.Direction).ConfigureAwait(false))
                continue;
            await LogDriftAsync(
                actionDrift ? EventKind.FirewallRuleChanged : EventKind.FirewallRuleCreated,
                rule, cancellationToken).ConfigureAwait(false);
            if (actionDrift) changed++; else added++;
        }

        // OS Beholder rules that should not exist (orphans, or anything while disabled).
        foreach (var osRule in osRules) {
            cancellationToken.ThrowIfCancellationRequested();
            if (expected.ContainsKey((osRule.ProcessPath, osRule.Direction))) continue;
            if (!await TryApplyAsync(
                    () => _controller.RemoveRuleAsync(osRule.ProcessPath, osRule.Direction, cancellationToken),
                    "remove", osRule.ProcessPath, osRule.Direction).ConfigureAwait(false))
                continue;
            await LogDriftAsync(EventKind.FirewallRuleRemoved, osRule, cancellationToken).ConfigureAwait(false);
            removed++;
        }

        if (added + changed + removed == 0) {
            _logger.LogInformation(
                "Firewall reconciliation: OS already in sync ({Count} Beholder rules)", osRules.Count);
        } else {
            _logger.LogWarning(
                "Firewall reconciliation corrected drift: {Added} re-applied, {Changed} action-fixed, {Removed} removed",
                added, changed, removed);
        }
    }

    private async Task<bool> TryApplyAsync(
        Func<Task> action, string verb, string processPath, Direction direction) {
        try {
            await action().ConfigureAwait(false);
            return true;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex,
                "Failed to {Verb} firewall rule for {ProcessPath} ({Direction}) during reconciliation",
                verb, processPath, direction);
            return false;
        }
    }

    private async Task LogDriftAsync(EventKind kind, FirewallRule rule, CancellationToken cancellationToken) {
        try {
            var payload = FirewallRulePayloadEncoder.Encode(rule);
            await _eventStore.AppendAsync(kind, payload, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex,
                "Reconciled a firewall rule for {ProcessPath} but failed to record it in the chain",
                rule.ProcessPath);
        }
    }
}
