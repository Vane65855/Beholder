using System;
using System.IO;
using Beholder.Protocol.Local;
using Beholder.Ui.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// One row in the Firewall tab's process table. Joins three sources:
/// <list type="bullet">
/// <item>Beholder rule store (the IN / OUT actions, if any),</item>
/// <item>live <see cref="Services.ProcessStateService"/> snapshot (the
///   IsActive flag and recent-window byte counters),</item>
/// <item>historical <c>GetProcessSummaries</c> result (lifetime byte totals
///   for processes that have no active state right now).</item>
/// </list>
/// All fields are observable so the parent VM can mutate the row in place
/// when an RPC succeeds or a broadcast event lands. Action enum has its own
/// value <see cref="FirewallActionState.Default"/> beyond proto's
/// Allow/Block — the proto enum has no "no-rule" value and we need it for
/// the three-state pill cycle.
/// </summary>
internal sealed partial class FirewallRuleRow : ObservableObject {
    public string ProcessPath { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private FirewallActionState _inAction = FirewallActionState.Default;

    [ObservableProperty]
    private FirewallActionState _outAction = FirewallActionState.Default;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private long _recentBytesTotal;

    [ObservableProperty]
    private RuleSource _source = RuleSource.Manual;

    /// <summary>
    /// True iff the daemon's rule store has a row for this process in either
    /// direction. Distinct from <see cref="InAction"/> / <see cref="OutAction"/>
    /// being non-Default because a row can briefly hold a transient state
    /// during optimistic UI updates. Drives <see cref="SourceLabel"/>'s
    /// blank-when-no-rule behavior so 84 ruleless rows don't all read as
    /// "manual" because of <see cref="RuleSource"/>'s zero default.
    /// </summary>
    [ObservableProperty]
    private bool _hasRule;

    /// <summary>
    /// Number of live TCP connections the daemon observed for this process
    /// at the last counter tick. Zero for inactive processes (no snapshot
    /// reports them). Sourced from <see cref="Services.ProcessState.ActiveConnectionCount"/>.
    /// </summary>
    [ObservableProperty]
    private int _activeConnectionCount;

    public FirewallRuleRow(string processPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ProcessPath = processPath;
        DisplayName = ExtractDisplayName(processPath);
    }

    /// <summary>
    /// Coarse status across both directions, used for header counts and
    /// row-level visual cues. Definitions match the plan:
    /// <list type="bullet">
    /// <item><c>Allowed</c> — both directions = Allow.</item>
    /// <item><c>Blocked</c> — both directions = Block.</item>
    /// <item><c>Partial</c> — directions differ AND at least one is Block.</item>
    /// <item><c>Default</c> — both directions = Default (no Beholder rule).</item>
    /// </list>
    /// </summary>
    public FirewallRowStatus OverallStatus {
        get {
            if (InAction == FirewallActionState.Allow && OutAction == FirewallActionState.Allow)
                return FirewallRowStatus.Allowed;
            if (InAction == FirewallActionState.Block && OutAction == FirewallActionState.Block)
                return FirewallRowStatus.Blocked;
            if (InAction == FirewallActionState.Default && OutAction == FirewallActionState.Default)
                return FirewallRowStatus.Default;
            // Differ AND at least one is Block — anything else lands here too,
            // e.g., Allow+Default which is genuinely partial coverage.
            return FirewallRowStatus.Partial;
        }
    }

    public string RecentBytesLabel => RecentBytesTotal == 0 ? "—" : ByteFormatter.FormatBytes(RecentBytesTotal);

    /// <summary>
    /// HOSTS column label. For active rows we surface the live TCP connection
    /// count; inactive rows render blank because we have no live snapshot
    /// (and the historical per-process destinations breakdown isn't on the
    /// wire today — adding it is a separate piece of work).
    /// </summary>
    public string HostsLabel => IsActive && ActiveConnectionCount > 0
        ? ActiveConnectionCount.ToString()
        : "—";

    public string SourceLabel => HasRule
        ? Source switch {
            RuleSource.Manual => "manual",
            RuleSource.Default => "default",
            RuleSource.Remote => "remote",
            _ => "—",
        }
        : "—";

    /// <summary>
    /// Cycle the action one step: <c>Allow → Block → Default → Allow</c>.
    /// </summary>
    public static FirewallActionState NextState(FirewallActionState current) => current switch {
        FirewallActionState.Allow => FirewallActionState.Block,
        FirewallActionState.Block => FirewallActionState.Default,
        FirewallActionState.Default => FirewallActionState.Allow,
        _ => FirewallActionState.Default,
    };

    /// <summary>
    /// Trims to filename-only for display. Falls back to the full path if
    /// extraction throws (malformed path) so the user always sees something.
    /// </summary>
    private static string ExtractDisplayName(string processPath) {
        try {
            var name = Path.GetFileName(processPath);
            return string.IsNullOrEmpty(name) ? processPath : name;
        } catch (ArgumentException) {
            return processPath;
        }
    }

    // Keep OverallStatus notifications wired to its inputs — ObservableObject
    // doesn't auto-detect computed-property dependencies.
    partial void OnInActionChanged(FirewallActionState value) => OnPropertyChanged(nameof(OverallStatus));
    partial void OnOutActionChanged(FirewallActionState value) => OnPropertyChanged(nameof(OverallStatus));
    partial void OnRecentBytesTotalChanged(long value) => OnPropertyChanged(nameof(RecentBytesLabel));
    partial void OnSourceChanged(RuleSource value) => OnPropertyChanged(nameof(SourceLabel));
    partial void OnHasRuleChanged(bool value) => OnPropertyChanged(nameof(SourceLabel));
    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(HostsLabel));
    partial void OnActiveConnectionCountChanged(int value) => OnPropertyChanged(nameof(HostsLabel));
}

/// <summary>
/// Three-state action for the per-direction pill: <c>Default</c> means "no
/// Beholder-managed rule exists" (Windows default = allow). Distinct from
/// <see cref="FirewallAction"/> in the wire protocol, which has only
/// Allow/Block — the absence of a rule is encoded in proto by simply not
/// returning one.
/// </summary>
internal enum FirewallActionState {
    Default = 0,
    Allow = 1,
    Block = 2,
}

/// <summary>
/// Coarse row-level summary status. Drives header counts (BLOCKED / PARTIAL)
/// and any future row-level visual cue.
/// </summary>
internal enum FirewallRowStatus {
    Default = 0,
    Allowed = 1,
    Blocked = 2,
    Partial = 3,
}
