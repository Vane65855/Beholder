using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Beholder.Core;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows;

/// <summary>
/// <see cref="IFirewallController"/> implementation backed by the native Windows Firewall
/// via the <c>INetFwPolicy2</c> COM interface (<c>HNetCfg.FwPolicy2</c>). Owns only the
/// rules it created — rules authored by other software are deliberately excluded from
/// <see cref="ListRulesAsync"/> by the <see cref="FirewallRuleNameEncoder"/> prefix filter.
///
/// COM activation is deferred behind a thread-safe <see cref="Lazy{T}"/> so construction
/// has no side effects and the type is safe to register in DI up-front; the first
/// firewall call triggers activation. All methods must be invoked on an Administrator
/// token — unelevated callers produce <see cref="COMException"/> with HRESULT
/// <c>0x80070005</c> (ERROR_ACCESS_DENIED), which is logged clearly and rethrown.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WfpFirewallController : IFirewallController, IDisposable {
    private const int ErrorFileNotFound = unchecked((int)0x80070002);
    private const int ErrorAccessDenied = unchecked((int)0x80070005);

    // INetFwRule.Direction: 1 = In, 2 = Out.
    private const int NetFwRuleDirIn = 1;
    private const int NetFwRuleDirOut = 2;

    // INetFwRule.Action: 0 = Block, 1 = Allow.
    private const int NetFwActionBlock = 0;
    private const int NetFwActionAllow = 1;

    // NET_FW_PROFILE2_ALL — Domain | Private | Public. Applied on every write so a
    // Beholder rule does not silently no-op when the interface is bound to a profile
    // the rule does not list.
    private const int NetFwProfileAll = 0x7;

    private readonly ILogger<WfpFirewallController> _logger;
    private readonly Lazy<dynamic> _firewallPolicy;
    private readonly SemaphoreSlim _comLock = new(1, 1);

    public WfpFirewallController(ILogger<WfpFirewallController> logger) {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _firewallPolicy = new Lazy<dynamic>(CreateFirewallPolicy, isThreadSafe: true);
    }

    public async Task<IReadOnlyList<FirewallRule>> ListRulesAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        await _comLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var result = new List<FirewallRule>();
            var now = DateTimeOffset.UtcNow;
            try {
                foreach (dynamic rule in _firewallPolicy.Value.Rules) {
                    string name = rule.Name as string ?? string.Empty;
                    if (!FirewallRuleNameEncoder.TryDecode(name, out var processPath, out var direction)) continue;

                    int actionCode = (int)rule.Action;
                    var action = actionCode == NetFwActionAllow ? FirewallAction.Allow : FirewallAction.Block;

                    result.Add(new FirewallRule(
                        id: 0,
                        processPath: processPath,
                        direction: direction,
                        action: action,
                        source: RuleSource.Manual,
                        createdAt: now,
                        updatedAt: now));
                }
            } catch (COMException ex) {
                LogComException(ex, "ListRules", string.Empty);
                throw;
            }
            return result;
        } finally {
            _comLock.Release();
        }
    }

    public async Task AddRuleAsync(FirewallRule rule, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(rule);
        cancellationToken.ThrowIfCancellationRequested();

        var name = FirewallRuleNameEncoder.Encode(rule.ProcessPath, rule.Direction);
        var directionCode = rule.Direction == Direction.Inbound ? NetFwRuleDirIn : NetFwRuleDirOut;
        var actionCode = rule.Action == FirewallAction.Allow ? NetFwActionAllow : NetFwActionBlock;

        await _comLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            try {
                dynamic rules = _firewallPolicy.Value.Rules;

                // Item(name) is the idiomatic existence check: it returns
                // HRESULT 0x80070002 (ERROR_FILE_NOT_FOUND) when no rule with
                // that name exists. .NET COM interop *translates* this HRESULT
                // to System.IO.FileNotFoundException — NOT COMException — for
                // INetFwRules.Item, empirically confirmed via probe. We match
                // by HResult rather than by exception type so the catch survives
                // however the runtime chooses to surface the same underlying
                // error code. Any other HResult is a real failure and propagates
                // through the outer catch.
                dynamic? existing = null;
                try {
                    existing = rules.Item(name);
                } catch (Exception ex) when (ex.HResult == ErrorFileNotFound) {
                    existing = null;
                }

                if (existing is not null) {
                    // Rewrite every mutable field — not just Action — so a hand-edited rule
                    // in wf.msc is snapped back to Beholder's canonical shape on next write.
                    existing.Action = actionCode;
                    existing.Direction = directionCode;
                    existing.ApplicationName = rule.ProcessPath;
                    existing.Enabled = true;
                    existing.Profiles = NetFwProfileAll;

                    _logger.LogInformation(
                        "Updated firewall rule {Direction} {Action} for {ProcessPath}",
                        rule.Direction, rule.Action, rule.ProcessPath);
                    return;
                }

                dynamic created = CreateNewRule();
                created.Name = name;
                created.ApplicationName = rule.ProcessPath;
                created.Action = actionCode;
                created.Direction = directionCode;
                created.Enabled = true;
                created.Profiles = NetFwProfileAll;
                rules.Add(created);

                _logger.LogInformation(
                    "Added firewall rule {Direction} {Action} for {ProcessPath}",
                    rule.Direction, rule.Action, rule.ProcessPath);
            } catch (COMException ex) {
                LogComException(ex, "AddRule", rule.ProcessPath);
                throw;
            }
        } finally {
            _comLock.Release();
        }
    }

    public async Task RemoveRuleAsync(string processPath, Direction direction, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        cancellationToken.ThrowIfCancellationRequested();

        var name = FirewallRuleNameEncoder.Encode(processPath, direction);

        await _comLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            try {
                _firewallPolicy.Value.Rules.Remove(name);
                _logger.LogInformation(
                    "Removed firewall rule {Direction} for {ProcessPath}", direction, processPath);
            } catch (Exception ex) when (ex.HResult == ErrorFileNotFound) {
                // Idempotent: removing a rule that does not exist is a success.
                // Match by HResult — Rules.Remove may surface the same 0x80070002
                // as either FileNotFoundException or COMException depending on
                // .NET COM interop's translation layer (same as Rules.Item above).
                _logger.LogInformation(
                    "RemoveRuleAsync: no existing rule to remove for {Direction} {ProcessPath}",
                    direction, processPath);
            } catch (COMException ex) {
                LogComException(ex, "RemoveRule", processPath);
                throw;
            }
        } finally {
            _comLock.Release();
        }
    }

    private static dynamic CreateFirewallPolicy() {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)
            ?? throw new InvalidOperationException(
                "HNetCfg.FwPolicy2 ProgID missing — the Windows Firewall service is not available.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException(
                "Failed to activate HNetCfg.FwPolicy2 COM instance.");
    }

    private static dynamic CreateNewRule() {
        var type = Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)
            ?? throw new InvalidOperationException(
                "HNetCfg.FWRule ProgID missing — the Windows Firewall service is not available.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException(
                "Failed to activate HNetCfg.FWRule COM instance.");
    }

    private void LogComException(COMException ex, string operation, string processPath) {
        if (ex.HResult == ErrorAccessDenied) {
            _logger.LogError(ex,
                "Access denied during {Operation} for {ProcessPath} — the daemon must run as Administrator to modify Windows Firewall",
                operation, processPath);
            return;
        }
        _logger.LogError(ex,
            "COM exception during {Operation} for {ProcessPath}: HRESULT=0x{HResult:X8}",
            operation, processPath, ex.HResult);
    }

    public void Dispose() => _comLock.Dispose();
}
