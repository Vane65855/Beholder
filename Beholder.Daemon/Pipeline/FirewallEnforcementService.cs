using Beholder.Core;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Reacts to <see cref="IFirewallEnforcementState"/> transitions by replaying
/// every persisted Beholder rule against the OS firewall. Toggling <c>off</c>
/// calls <see cref="IFirewallController.RemoveRuleAsync"/> for each persisted
/// rule (SQLite copies are kept so a subsequent <c>on</c> can re-apply them);
/// toggling <c>on</c> calls <see cref="IFirewallController.AddRuleAsync"/>.
/// Both directions are idempotent at the controller layer, so a redundant
/// transition is a no-op even if state drifts.
/// </summary>
/// <remarks>
/// <para>
/// Concurrency: <see cref="IFirewallEnforcementState.StateChanged"/> fires
/// synchronously from the RPC thread, but enumeration runs as a fire-and-
/// forget <see cref="Task"/> so the RPC returns promptly. The controller
/// implementations serialize via their own <c>SemaphoreSlim</c>, so an
/// in-flight toggle and a concurrent <c>ApplyFirewallRule</c> RPC interleave
/// safely at OS-API granularity.
/// </para>
/// <para>
/// Errors per rule are logged and swallowed — a failure on one rule must not
/// abort enforcement for the rest. The service does not retry; if a rule
/// fails to apply during a toggle, the user can flip the master switch off
/// and on again to retry.
/// </para>
/// </remarks>
internal sealed class FirewallEnforcementService : IHostedService {
    private readonly IFirewallEnforcementState _state;
    private readonly IFirewallRuleStore _ruleStore;
    private readonly IFirewallController _controller;
    private readonly ILogger<FirewallEnforcementService> _logger;

    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _subscribed;

    public FirewallEnforcementService(
        IFirewallEnforcementState state,
        IFirewallRuleStore ruleStore,
        IFirewallController controller,
        ILogger<FirewallEnforcementService> logger
    ) {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ruleStore);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(logger);
        _state = state;
        _ruleStore = ruleStore;
        _controller = controller;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        _state.StateChanged += OnStateChanged;
        _subscribed = true;
        _logger.LogInformation(
            "Firewall enforcement service started (initial state: {Enabled})", _state.Enabled);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        if (_subscribed) {
            _state.StateChanged -= OnStateChanged;
            _subscribed = false;
        }
        await _shutdownCts.CancelAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();
    }

    private void OnStateChanged(bool enabled) {
        // Fire and forget: the state change must not block the RPC handler.
        // Errors propagate into ApplyEnforcementAsync's catch.
        _ = Task.Run(() => ApplyEnforcementAsync(enabled, _shutdownCts.Token));
    }

    /// <summary>
    /// Test seam: synchronously enumerates the rule store and pushes each rule
    /// through the controller. Public to <c>internal</c> tests so they can
    /// observe the apply/remove sequence without timing on the fire-and-forget
    /// task spawned by <see cref="OnStateChanged"/>.
    /// </summary>
    internal async Task ApplyEnforcementAsync(bool enabled, CancellationToken cancellationToken) {
        IReadOnlyList<FirewallRule> rules;
        try {
            rules = await _ruleStore.ListAllAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _logger.LogError(ex,
                "Failed to enumerate firewall rules during enforcement toggle to {Enabled}",
                enabled);
            return;
        }

        var succeeded = 0;
        var failed = 0;
        foreach (var rule in rules) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                if (enabled) {
                    await _controller.AddRuleAsync(rule, cancellationToken).ConfigureAwait(false);
                } else {
                    await _controller.RemoveRuleAsync(
                        rule.ProcessPath, rule.Direction, cancellationToken).ConfigureAwait(false);
                }
                succeeded++;
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                _logger.LogError(ex,
                    "Failed to {Action} firewall rule for {ProcessPath} ({Direction}) during enforcement toggle",
                    enabled ? "apply" : "remove", rule.ProcessPath, rule.Direction);
                failed++;
            }
        }

        _logger.LogInformation(
            "Firewall enforcement toggle to {Enabled} replayed {Succeeded} rules ({Failed} failed)",
            enabled, succeeded, failed);
    }
}
