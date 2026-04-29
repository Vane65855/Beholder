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

    /// <summary>
    /// Whether the executable at <see cref="ProcessPath"/> currently exists
    /// on disk. Defaults to <c>true</c> (optimistic) and is set authoritatively
    /// during the tab's initial join via a one-shot <c>File.Exists</c> check.
    /// Live processes (i.e., <see cref="IsActive"/> = true) are forced back
    /// to <c>true</c> on every counter snapshot — a process that's currently
    /// reporting traffic must have its executable on disk.
    /// </summary>
    [ObservableProperty]
    private bool _executableExists = true;

    /// <summary>
    /// True when this row points at a process whose executable is gone but a
    /// manual firewall rule still references it. Drives the warning icon
    /// in the table, the bottom-of-Inactive sort position, and the row's
    /// retention in the visible list (rows where the executable is gone *and*
    /// no rule remains are filtered out entirely — they're noise).
    /// </summary>
    public bool IsOrphanedRule => HasRule && !ExecutableExists;

    /// <summary>
    /// Transient flag set true when the user follows an "ADD RULE" deep-link
    /// from the Alerts tab, cleared after ~2 s. The view binds
    /// <c>Classes.highlighted="{Binding IsHighlighted}"</c> on the row's
    /// <c>Border.ruleRow</c> so a brief accent border draws the user's eye
    /// to the right row after a tab switch. Mirror of
    /// <c>FirewallTabViewModel.NotifyPathCopied</c>'s transient-banner
    /// pattern.
    /// </summary>
    [ObservableProperty]
    private bool _isHighlighted;

    public FirewallRuleRow(string processPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ProcessPath = processPath;
        DisplayName = ExtractDisplayName(processPath);
    }

    /// <summary>
    /// Coarse status across both directions, used for header counts and
    /// row-level visual cues. The pill UI is a status indicator for
    /// effective connectivity (not the underlying rule state), so
    /// <c>Default</c> and <c>Allow</c> both contribute to the "allowed"
    /// bucket — there's no observable difference between "no rule" and
    /// "explicit allow rule" at this surface.
    /// <list type="bullet">
    /// <item><c>Blocked</c> — both directions are <c>Block</c>.</item>
    /// <item><c>Partial</c> — exactly one direction is <c>Block</c>.</item>
    /// <item><c>Allowed</c> — neither direction is <c>Block</c>
    ///   (covers Default+Default, Allow+Allow, and any Default/Allow mix).</item>
    /// </list>
    /// <see cref="FirewallRowStatus.Default"/> remains in the enum as the
    /// initial-pre-data sentinel but is no longer returned here.
    /// </summary>
    public FirewallRowStatus OverallStatus {
        get {
            var inIsBlock = InAction == FirewallActionState.Block;
            var outIsBlock = OutAction == FirewallActionState.Block;
            return (inIsBlock, outIsBlock) switch {
                (true, true) => FirewallRowStatus.Blocked,
                (false, false) => FirewallRowStatus.Allowed,
                _ => FirewallRowStatus.Partial,
            };
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

    // No Beholder rule means the system default applies (Windows allows by
    // default), so the SOURCE column reads "DEFAULT" — not "—". The em-dash
    // is reserved for the genuinely-unknown defensive fallback below
    // (unrecognized RuleSource enum values, which shouldn't occur in
    // practice). IsSourceDefault below drives the row's foreground color
    // via Classes.muted in the view.
    public string SourceLabel => HasRule
        ? Source switch {
            RuleSource.Manual => "MANUAL",
            RuleSource.Default => "DEFAULT",
            RuleSource.Remote => "REMOTE",
            _ => "—",
        }
        : "DEFAULT";

    /// <summary>
    /// True when the SOURCE column should render in the muted foreground
    /// (TextMuted): either no Beholder rule exists, or an explicit
    /// <see cref="RuleSource.Default"/> rule applies — both cases mean
    /// "system default applies" and recede into the baseline. Manual and
    /// Remote rules lift one shade brighter to mark "this row has a real
    /// rule." Drives <c>Classes.muted</c> on the SOURCE TextBlock.
    /// </summary>
    public bool IsSourceDefault => !HasRule || Source == RuleSource.Default;

    /// <summary>
    /// Binary toggle: any non-<c>Block</c> state goes to <c>Block</c>;
    /// <c>Block</c> goes to <c>Default</c> (rule removed). The pill UI
    /// only ever shows two effective states (ALLOW / BLOCK), so the data
    /// transitions follow suit.
    /// </summary>
    public static FirewallActionState NextState(FirewallActionState current) => current switch {
        FirewallActionState.Block => FirewallActionState.Default,
        // Default and Allow both transition to Block. The previous three-state
        // cycle (Allow → Block → Default → Allow) was deprecated when the pill
        // became a status indicator rather than a rule editor.
        _ => FirewallActionState.Block,
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
    partial void OnSourceChanged(RuleSource value) {
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(IsSourceDefault));
    }
    partial void OnHasRuleChanged(bool value) {
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(IsOrphanedRule));
        OnPropertyChanged(nameof(IsSourceDefault));
    }
    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(HostsLabel));
    partial void OnActiveConnectionCountChanged(int value) => OnPropertyChanged(nameof(HostsLabel));
    partial void OnExecutableExistsChanged(bool value) => OnPropertyChanged(nameof(IsOrphanedRule));
}
