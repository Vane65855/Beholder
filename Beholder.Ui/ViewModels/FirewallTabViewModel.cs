using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Backs the Firewall tab. Joins three sources into a single
/// <see cref="FirewallRuleRow"/> stream:
/// <list type="bullet">
/// <item>persisted rules from <c>ListFirewallRules</c>,</item>
/// <item>live process states from <see cref="ProcessStateService"/>,</item>
/// <item>historical process summaries (all-time) from
/// <c>GetProcessSummaries</c>.</item>
/// </list>
/// Rule mutations go through <c>ApplyFirewallRule</c> /
/// <c>RemoveFirewallRule</c> with optimistic UI; the daemon's broadcast
/// stream is the source of truth for confirmation.
/// </summary>
internal sealed partial class FirewallTabViewModel : ViewModelBase, IDisposable {
    private readonly IDaemonClient _daemonClient;
    private readonly ProcessStateService _processStateService;
    private readonly DaemonStreamSubscriber _streamSubscriber;
    private readonly Func<string, bool> _fileExistsCheck;
    private readonly Dictionary<string, FirewallRuleRow> _rowsByPath = new(StringComparer.Ordinal);

    private CancellationTokenSource? _activationCts;
    private bool _activated;

    /// <summary>
    /// Activity strip child VM. Owned by the Firewall tab so its lifecycle
    /// matches the parent (single dispose, single activation), but exposed
    /// to the view via a public property so the strip can bind directly.
    /// </summary>
    public FirewallActivityViewModel ActivityVm { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilteredActiveRows))]
    [NotifyPropertyChangedFor(nameof(HasFilteredInactiveRows))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilteredActiveRows))]
    [NotifyPropertyChangedFor(nameof(HasFilteredInactiveRows))]
    private FirewallFilter _selectedFilter = FirewallFilter.All;

    [ObservableProperty]
    private bool _isFirewallEnabled = true;

    /// <summary>
    /// Transient feedback shown after the user double-clicks a row to copy
    /// its parent directory to the clipboard. Auto-clears after 2 seconds
    /// via <see cref="ClearTransientMessageAfterDelayAsync"/>; a second copy
    /// within the window cancels the prior timer and starts a new one (last
    /// copy wins, no premature clear).
    /// </summary>
    [ObservableProperty]
    private string _transientMessage = string.Empty;

    [ObservableProperty]
    private bool _hasTransientMessage;

    private CancellationTokenSource? _transientMessageCts;

    /// <summary>
    /// Whether the ACTIVE APPS group renders its rows. Default true: this is
    /// the user's primary working set so they see it immediately on tab open.
    /// </summary>
    [ObservableProperty]
    private bool _isActiveExpanded = true;

    /// <summary>
    /// Whether the INACTIVE APPS group renders its rows. Default false: with
    /// 78+ inactive processes on a long-running daemon, expanding by default
    /// dumps the user into a long scroll. Click the group header to expand.
    /// Per the original Phase 6.4 plan: "Active expanded by default; Inactive
    /// collapsed by default; click to expand."
    /// </summary>
    [ObservableProperty]
    private bool _isInactiveExpanded;

    /// <summary>
    /// Active processes — those currently visible to <see cref="ProcessStateService"/>.
    /// Sorted by display name for stable ordering across snapshots.
    /// </summary>
    public ObservableCollection<FirewallRuleRow> ActiveRows { get; } = new();

    /// <summary>
    /// Processes Beholder has seen historically but that are not currently
    /// active. Includes any process that ever made a flow plus any persisted
    /// rule whose process hasn't been re-observed yet.
    /// </summary>
    public ObservableCollection<FirewallRuleRow> InactiveRows { get; } = new();

    public IEnumerable<FirewallRuleRow> FilteredActiveRows =>
        ActiveRows.Where(MatchesSearchAndFilter);

    public IEnumerable<FirewallRuleRow> FilteredInactiveRows =>
        InactiveRows.Where(MatchesSearchAndFilter);

    public bool HasRows => !IsLoading && (ActiveRows.Count + InactiveRows.Count) > 0;

    public bool HasFilteredActiveRows => FilteredActiveRows.Any();
    public bool HasFilteredInactiveRows => FilteredInactiveRows.Any();

    /// <summary>Header counts: total processes across both groups.</summary>
    public int TotalProcessCount => ActiveRows.Count + InactiveRows.Count;

    /// <summary>Header counts: rows whose IN+OUT both = Block.</summary>
    public int BlockedProcessCount =>
        ActiveRows.Concat(InactiveRows).Count(r => r.OverallStatus == FirewallRowStatus.Blocked);

    /// <summary>Header counts: rows with at least one Block alongside something else.</summary>
    public int PartialProcessCount =>
        ActiveRows.Concat(InactiveRows).Count(r => r.OverallStatus == FirewallRowStatus.Partial);

    public FirewallTabViewModel(
        IDaemonClient daemonClient,
        ProcessStateService processStateService,
        DaemonStreamSubscriber streamSubscriber,
        Func<string, bool>? fileExistsCheck = null
    ) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(processStateService);
        ArgumentNullException.ThrowIfNull(streamSubscriber);
        _daemonClient = daemonClient;
        _processStateService = processStateService;
        _streamSubscriber = streamSubscriber;
        // Optional injection so tests can simulate uninstalled apps deterministically
        // without touching the real filesystem.
        _fileExistsCheck = fileExistsCheck ?? File.Exists;
        ActivityVm = new FirewallActivityViewModel(daemonClient, streamSubscriber);

        _processStateService.ProcessStatesUpdated += OnProcessStatesUpdated;
        _streamSubscriber.RuleChangeReceived += OnRuleChange;
        _daemonClient.StateChanged += OnDaemonStateChanged;
    }

    public void Dispose() {
        _processStateService.ProcessStatesUpdated -= OnProcessStatesUpdated;
        _streamSubscriber.RuleChangeReceived -= OnRuleChange;
        _daemonClient.StateChanged -= OnDaemonStateChanged;
        ActivityVm.Dispose();
        _activationCts?.Cancel();
        _activationCts?.Dispose();
        _transientMessageCts?.Cancel();
        _transientMessageCts?.Dispose();
    }

    /// <summary>
    /// Initial load. Idempotent — calling more than once short-circuits after
    /// the first activation. Tests and the tab-switch path both invoke this;
    /// the second call is a no-op so repeated tab switches don't re-fetch.
    /// </summary>
    public async Task ActivateAsync(CancellationToken cancellationToken) {
        if (_activated) return;
        _activated = true;
        _activationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Activity strip and rule list fetch in parallel — both are cheap
        // queries against SQLite and they share no failure mode, so a stuck
        // strip wouldn't gate the table.
        var ruleLoad = ReloadAsync(_activationCts.Token);
        var activityLoad = ActivityVm.ActivateAsync(_activationCts.Token);
        await Task.WhenAll(ruleLoad, activityLoad);
    }

    private async Task ReloadAsync(CancellationToken cancellationToken) {
        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;

        try {
            // Fan out the three queries in parallel — they're all read-only
            // and independent. The snapshot also gives us the master toggle
            // state, so we don't need a separate RPC just to render the
            // header pill.
            var rulesTask = _daemonClient.ListFirewallRulesAsync(
                new ListFirewallRulesRequest(), cancellationToken);
            var snapshotTask = _daemonClient.GetSnapshotAsync(cancellationToken);
            var summariesTask = _daemonClient.GetProcessSummariesAsync(
                new GetProcessSummariesRequest {
                    FromUnixNs = 0,
                    ToUnixNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L,
                },
                cancellationToken);

            await Task.WhenAll(rulesTask, snapshotTask, summariesTask);

            var rules = rulesTask.Result.Rules;
            var snapshot = snapshotTask.Result;
            var summaries = summariesTask.Result.Summaries;

            ApplyInitialData(rules, summaries);
            IsFirewallEnabled = snapshot.FirewallEnforcementEnabled;
        } catch (OperationCanceledException) {
            // Tab disposed mid-load — drop it silently.
        } catch (RpcException ex) {
            HasError = true;
            ErrorMessage = $"Failed to load firewall rules: {ex.Status.Detail}";
        } catch (Exception ex) {
            HasError = true;
            ErrorMessage = $"Failed to load firewall rules: {ex.Message}";
        } finally {
            IsLoading = false;
            NotifyHeaderCountsChanged();
        }
    }

    private void ApplyInitialData(
        IReadOnlyList<FirewallRule> rules,
        IReadOnlyList<ProcessTrafficSummaryProto> summaries
    ) {
        // Build the row set from the union of (any process with a rule) +
        // (any process ever seen on the wire). Live state from the
        // ProcessStateService gets folded in via OnProcessStatesUpdated
        // independently — its event fires on the subscriber thread and we
        // don't want to block the initial load on it.
        _rowsByPath.Clear();
        foreach (var rule in rules) {
            if (IsExcludedProcess(rule.ProcessPath)) continue;
            var row = GetOrCreateRow(rule.ProcessPath);
            ApplyRuleToRow(row, rule);
        }
        foreach (var summary in summaries) {
            if (IsExcludedProcess(summary.ProcessPath)) continue;
            var row = GetOrCreateRow(summary.ProcessPath);
            row.RecentBytesTotal = summary.TotalBytesIn + summary.TotalBytesOut;
        }

        // One-shot file-existence check across every row. ~80 paths * single
        // stat call is sub-millisecond on warm caches; running on the UI thread
        // here is fine because ActivateAsync is already async-bound to the RPC
        // round-trips that just completed.
        foreach (var row in _rowsByPath.Values) {
            row.ExecutableExists = SafeFileExists(row.ProcessPath);
        }

        Reclassify();
    }

    /// <summary>
    /// Wraps the injected <see cref="_fileExistsCheck"/> in a try/catch so
    /// permission errors or malformed paths can't kill the join. We default to
    /// "exists" on exception — better to over-show a row than to drop one
    /// the user might want to act on.
    /// </summary>
    private bool SafeFileExists(string path) {
        try { return _fileExistsCheck(path); }
        catch { return true; }
    }

    private FirewallRuleRow GetOrCreateRow(string processPath) {
        if (_rowsByPath.TryGetValue(processPath, out var existing)) return existing;
        var row = new FirewallRuleRow(processPath);
        _rowsByPath[processPath] = row;
        return row;
    }

    /// <summary>
    /// Filters non-controllable pseudo-processes out of the rule table. The
    /// Firewall tab is a *rule* surface; processes that <c>INetFwPolicy2</c>
    /// rejects rules against don't belong here. Currently just <c>"System"</c>
    /// (the kernel pseudo-process surfaced by ETW counters); future entries
    /// might include <c>"Idle"</c> and unknown-PID rows if we ever encounter
    /// them.
    /// </summary>
    private static bool IsExcludedProcess(string processPath) =>
        string.Equals(processPath, "System", StringComparison.Ordinal);

    private static void ApplyRuleToRow(FirewallRuleRow row, FirewallRule rule) {
        var actionState = rule.Action == FirewallAction.Block
            ? FirewallActionState.Block
            : FirewallActionState.Allow;
        if (rule.Direction == Direction.Inbound) row.InAction = actionState;
        else row.OutAction = actionState;
        row.Source = rule.Source;
        row.HasRule = true;
    }

    private void OnProcessStatesUpdated(IReadOnlyDictionary<string, ProcessState> states) {
        Dispatcher.UIThread.Post(() => {
            // Mark every row inactive first, then flip the ones we see live.
            // This handles processes disappearing from the live snapshot
            // (process exited) as well as new ones appearing.
            foreach (var row in _rowsByPath.Values) {
                row.IsActive = false;
                row.ActiveConnectionCount = 0;
            }
            foreach (var (path, state) in states) {
                if (IsExcludedProcess(path)) continue;
                var row = GetOrCreateRow(path);
                row.IsActive = true;
                // A process that's currently reporting traffic must have its
                // executable on disk — flip the orphan flag back if a previously-
                // missing app got reinstalled and re-launched mid-session.
                row.ExecutableExists = true;
                row.ActiveConnectionCount = state.ActiveConnectionCount;
                // RecentBytesTotal: prefer live deltas over the historical
                // summary value because the live values reflect very-recent
                // activity.
                row.RecentBytesTotal = state.TotalBytesIn + state.TotalBytesOut;
            }
            Reclassify();
            NotifyHeaderCountsChanged();
        });
    }

    private void OnRuleChange(FirewallRuleChange change) {
        Dispatcher.UIThread.Post(() => {
            if (IsExcludedProcess(change.Rule.ProcessPath)) return;
            switch (change.Change) {
                case FirewallRuleChange.Types.ChangeKind.Created:
                case FirewallRuleChange.Types.ChangeKind.Changed:
                    var existed = _rowsByPath.ContainsKey(change.Rule.ProcessPath);
                    var row = GetOrCreateRow(change.Rule.ProcessPath);
                    ApplyRuleToRow(row, change.Rule);
                    if (!existed) {
                        // First time we're seeing this path — run the existence
                        // check so a freshly-broadcast rule against a deleted
                        // app shows up correctly as orphaned.
                        row.ExecutableExists = SafeFileExists(change.Rule.ProcessPath);
                    }
                    EnsureMembership(row);
                    break;
                case FirewallRuleChange.Types.ChangeKind.Removed:
                    if (_rowsByPath.TryGetValue(change.Rule.ProcessPath, out var existing)) {
                        if (change.Rule.Direction == Direction.Inbound)
                            existing.InAction = FirewallActionState.Default;
                        else
                            existing.OutAction = FirewallActionState.Default;
                        // If both directions are now Default, the row no longer
                        // has any persisted rule — flip HasRule off so SourceLabel
                        // returns to "—" rather than the stale "manual" stamp.
                        if (existing.InAction == FirewallActionState.Default
                            && existing.OutAction == FirewallActionState.Default) {
                            existing.HasRule = false;
                            // If the executable is also gone, the row no longer
                            // qualifies for visibility (orphaned -> noise). Run
                            // Reclassify to drop it from InactiveRows.
                            if (!existing.ExecutableExists) {
                                Reclassify();
                            }
                        }
                    }
                    break;
            }
            NotifyHeaderCountsChanged();
            OnPropertyChanged(nameof(FilteredActiveRows));
            OnPropertyChanged(nameof(FilteredInactiveRows));
            OnPropertyChanged(nameof(HasFilteredActiveRows));
            OnPropertyChanged(nameof(HasFilteredInactiveRows));
        });
    }

    private void OnDaemonStateChanged(DaemonStatusInfo status) {
        Dispatcher.UIThread.Post(() => {
            if (status.State is ConnectionState.Disconnected or ConnectionState.Reconnecting) {
                HasError = true;
                ErrorMessage = "Daemon disconnected — showing last known state.";
            } else if (status.State == ConnectionState.Connected) {
                HasError = false;
                ErrorMessage = string.Empty;
            }
        });
    }

    private void Reclassify() {
        ActiveRows.Clear();
        InactiveRows.Clear();

        // Active group: every running process, alphabetical. IsActive implies
        // ExecutableExists so no filtering needed here.
        foreach (var row in _rowsByPath.Values
            .Where(r => r.IsActive)
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)) {
            ActiveRows.Add(row);
        }

        // Inactive group: existing apps first, then orphaned-rule apps. Rows
        // where the executable is gone *and* no rule references them are
        // dropped — they're noise (uninstalled apps with no actionable state).
        var inactive = _rowsByPath.Values.Where(r => !r.IsActive).ToList();
        foreach (var row in inactive
            .Where(r => r.ExecutableExists)
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)) {
            InactiveRows.Add(row);
        }
        foreach (var row in inactive
            .Where(r => r.IsOrphanedRule)
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)) {
            InactiveRows.Add(row);
        }

        OnPropertyChanged(nameof(FilteredActiveRows));
        OnPropertyChanged(nameof(FilteredInactiveRows));
        OnPropertyChanged(nameof(HasFilteredActiveRows));
        OnPropertyChanged(nameof(HasFilteredInactiveRows));
        OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>
    /// Fast-path move of a single row between groups when its IsActive flag
    /// changes via a broadcast. Avoids a full <see cref="Reclassify"/> which
    /// rebuilds both ObservableCollections.
    /// </summary>
    private void EnsureMembership(FirewallRuleRow row) {
        var inActive = ActiveRows.Contains(row);
        var inInactive = InactiveRows.Contains(row);
        if (row.IsActive) {
            if (!inActive) ActiveRows.Add(row);
            if (inInactive) InactiveRows.Remove(row);
        } else {
            if (!inInactive) InactiveRows.Add(row);
            if (inActive) ActiveRows.Remove(row);
        }
    }

    private bool MatchesSearchAndFilter(FirewallRuleRow row) {
        // Search: matches name OR path, case-insensitive.
        if (!string.IsNullOrWhiteSpace(SearchText)) {
            var query = SearchText.Trim();
            if (row.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                && row.ProcessPath.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) {
                return false;
            }
        }

        return SelectedFilter switch {
            FirewallFilter.All => true,
            FirewallFilter.ActiveOnly => row.IsActive,
            FirewallFilter.InactiveOnly => !row.IsActive,
            FirewallFilter.Blocked => row.OverallStatus == FirewallRowStatus.Blocked,
            FirewallFilter.Partial => row.OverallStatus == FirewallRowStatus.Partial,
            _ => true,
        };
    }

    /// <summary>
    /// Three-state pill click. The next state comes from
    /// <see cref="FirewallRuleRow.NextState"/>, then we dispatch the right
    /// RPC: Block/Allow → Apply, Default → Remove. UI updates optimistically;
    /// any RPC failure reverts the state and surfaces a transient banner.
    /// </summary>
    [RelayCommand]
    private async Task CycleInPill(FirewallRuleRow row) {
        if (row is null) return;
        var previous = row.InAction;
        var next = FirewallRuleRow.NextState(previous);
        row.InAction = next;
        try {
            await DispatchPillRpcAsync(row.ProcessPath, Direction.Inbound, next, CancellationToken.None);
            NotifyHeaderCountsChanged();
        } catch (Exception ex) {
            row.InAction = previous;
            HasError = true;
            ErrorMessage = $"Failed to update IN rule: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CycleOutPill(FirewallRuleRow row) {
        if (row is null) return;
        var previous = row.OutAction;
        var next = FirewallRuleRow.NextState(previous);
        row.OutAction = next;
        try {
            await DispatchPillRpcAsync(row.ProcessPath, Direction.Outbound, next, CancellationToken.None);
            NotifyHeaderCountsChanged();
        } catch (Exception ex) {
            row.OutAction = previous;
            HasError = true;
            ErrorMessage = $"Failed to update OUT rule: {ex.Message}";
        }
    }

    private async Task DispatchPillRpcAsync(
        string processPath, Direction direction, FirewallActionState target, CancellationToken cancellationToken
    ) {
        // The pill is a binary toggle: target is always either Block (apply
        // a block rule) or Default (remove the existing rule). Allow targets
        // are not reachable from clicks under the status-indicator model —
        // an explicit-allow rule is a future power-user surface, not a
        // by-product of the toggle.
        switch (target) {
            case FirewallActionState.Block:
                await _daemonClient.ApplyFirewallRuleAsync(new ApplyFirewallRuleRequest {
                    ProcessPath = processPath,
                    Direction = direction,
                    Action = FirewallAction.Block,
                    Source = RuleSource.Manual,
                }, cancellationToken);
                break;
            case FirewallActionState.Default:
                await _daemonClient.RemoveFirewallRuleAsync(new RemoveFirewallRuleRequest {
                    ProcessPath = processPath,
                    Direction = direction,
                }, cancellationToken);
                break;
            // FirewallActionState.Allow is intentionally unreachable here —
            // NextState never produces it. If a future caller wires a path
            // that does, it should call ApplyFirewallRuleAsync(Allow) explicitly
            // rather than going through this toggle helper.
        }
    }

    /// <summary>
    /// Toggles the ACTIVE APPS group's expanded state. Bound to a borderless
    /// button wrapping the group header in the view.
    /// </summary>
    [RelayCommand]
    private void ToggleActiveExpanded() => IsActiveExpanded = !IsActiveExpanded;

    /// <summary>
    /// Toggles the INACTIVE APPS group's expanded state. Default-collapsed
    /// per the original Phase 6.4 plan to avoid dumping the user into a
    /// long scroll of inactive processes on tab open.
    /// </summary>
    [RelayCommand]
    private void ToggleInactiveExpanded() => IsInactiveExpanded = !IsInactiveExpanded;

    [RelayCommand]
    private async Task ToggleEnforcement() {
        var previous = IsFirewallEnabled;
        var target = !previous;
        IsFirewallEnabled = target;
        try {
            var response = await _daemonClient.SetFirewallEnabledAsync(
                new SetFirewallEnabledRequest { Enabled = target }, CancellationToken.None);
            // Trust the server's echo — it's authoritative if a concurrent
            // change happened.
            IsFirewallEnabled = response.Enabled;
        } catch (Exception ex) {
            IsFirewallEnabled = previous;
            HasError = true;
            ErrorMessage = $"Failed to toggle firewall enforcement: {ex.Message}";
        }
    }

    partial void OnSearchTextChanged(string value) {
        OnPropertyChanged(nameof(FilteredActiveRows));
        OnPropertyChanged(nameof(FilteredInactiveRows));
    }

    partial void OnSelectedFilterChanged(FirewallFilter value) {
        OnPropertyChanged(nameof(FilteredActiveRows));
        OnPropertyChanged(nameof(FilteredInactiveRows));
    }

    private void NotifyHeaderCountsChanged() {
        OnPropertyChanged(nameof(TotalProcessCount));
        OnPropertyChanged(nameof(BlockedProcessCount));
        OnPropertyChanged(nameof(PartialProcessCount));
    }

    /// <summary>
    /// Called by the view code-behind after a row's parent directory is
    /// copied to the clipboard via double-tap. Sets the transient banner's
    /// state and starts a 2-second auto-clear timer; a second copy within
    /// that window cancels the prior timer so the latest message survives.
    /// Empty / whitespace input is silently ignored — the view should not
    /// invoke this for paths that have no directory component.
    /// </summary>
    public void NotifyPathCopied(string directoryPath) {
        if (string.IsNullOrWhiteSpace(directoryPath)) return;

        TransientMessage = $"Copied: {directoryPath}";
        HasTransientMessage = true;

        // Cancel any prior pending auto-clear so a second copy within the
        // 2-second window doesn't get prematurely dismissed by the first
        // copy's timer.
        _transientMessageCts?.Cancel();
        _transientMessageCts?.Dispose();
        _transientMessageCts = new CancellationTokenSource();
        _ = ClearTransientMessageAfterDelayAsync(_transientMessageCts.Token);
    }

    private async Task ClearTransientMessageAfterDelayAsync(CancellationToken cancellationToken) {
        try {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(true);
            if (!cancellationToken.IsCancellationRequested) {
                HasTransientMessage = false;
                TransientMessage = string.Empty;
            }
        } catch (OperationCanceledException) {
            // Superseded by a later NotifyPathCopied — newer call owns the
            // state, leave it intact.
        }
    }
}

/// <summary>
/// Filter dropdown values for the Firewall tab header.
/// </summary>
internal enum FirewallFilter {
    All = 0,
    ActiveOnly = 1,
    InactiveOnly = 2,
    Blocked = 3,
    Partial = 4,
}
