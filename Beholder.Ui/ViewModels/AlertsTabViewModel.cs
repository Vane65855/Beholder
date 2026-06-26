using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
// Beholder.Core enums ordinally match Beholder.Protocol.Local enums by design
// (per phases.md Phase 0 design decision). We alias INotificationService as
// the only Core type touched here; the AlertKind cast at the Notify call
// site is safe by ordinal compatibility.
using INotificationService = Beholder.Core.INotificationService;
using CoreAlertKind = Beholder.Core.AlertKind;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Backs the Alerts tab. Master-detail layout: a list of alert rows on the
/// left (newest-first, virtualized), a detail pane on the right with the
/// selected alert's full context plus the two action buttons (BLOCK PROCESS
/// OUT, ADD RULE).
/// </summary>
/// <remarks>
/// <para>Two data sources:</para>
/// <list type="bullet">
/// <item>Initial fetch: the existing <c>GetSnapshotAsync.RecentAlerts</c>
///   field (capped at 100 server-side). No new RPC needed.</item>
/// <item>Live updates: <see cref="DaemonStreamSubscriber.AlertReceived"/>
///   event. Daemon-side broadcaster (Phase 6.6 commit 1) is wired but
///   currently has no caller — Phase 7's detectors will trigger it. Until
///   then this VM's empty state is the production-default.</item>
/// </list>
/// <para>Mark-read uses optimistic UI: <see cref="AlertRow.IsRead"/> flips
/// immediately when the user selects an unread alert; the
/// <see cref="MarkAlertReadRequest"/> RPC fires in the background and the
/// flip is reverted only on RPC failure.</para>
/// </remarks>
internal sealed partial class AlertsTabViewModel : ViewModelBase, IDisposable {
    /// <summary>Live-cap on the in-memory alert list. Mirrors
    /// <c>FirewallActivityViewModel.MaxRetainedEvents</c>.</summary>
    private const int MaxRetainedAlerts = 500;

    private readonly IDaemonClient _daemonClient;
    private readonly DaemonStreamSubscriber _streamSubscriber;
    private readonly IDispatcher _dispatcher;
    private readonly INotificationService _notifications;
    private readonly Func<string, Task>? _navigateToFirewallRule;
    private readonly Func<string, bool> _fileExistsCheck;
    private readonly HashSet<long> _seenSeqs = new();

    /// <summary>
    /// Process paths with an active Outbound + Block firewall rule. Seeded
    /// from <c>GetSnapshotResponse.FirewallRules</c> at activation and
    /// updated from the daemon's live <c>RuleChange</c> broadcast. Drives
    /// the detail-pane BLOCK / UNBLOCK toggle on every <see cref="AlertRow"/>
    /// whose <c>ProcessPath</c> is a member.
    /// </summary>
    private readonly HashSet<string> _outboundBlockedPaths =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _activationCts;

    /// <summary>
    /// In-flight (or completed) activation task. Tracking the Task instead of
    /// a plain <c>bool _activated</c> closes the cold-start race the toast →
    /// <c>NavigateToAlertAsync</c> deep-link hits: <c>OnActiveTabChanged</c>
    /// fires <c>ActivateAsync</c> as fire-and-forget, then
    /// <c>NavigateToAlertAsync</c> awaits its own call. With a bool guard,
    /// the second call would see <c>_activated = true</c> and return
    /// instantly while the first call's snapshot RPC was still in flight,
    /// leaving <see cref="Alerts"/> empty when <c>SelectBySeq</c> ran.
    /// Storing the Task makes both callers await the same underlying load.
    /// Mirrors the same fix in <c>FirewallTabViewModel</c> (commit 2cb8753).
    /// </summary>
    private Task? _activationTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowLoadingState))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetailPane))]
    private AlertRow? _selectedAlert;

    public ObservableCollection<AlertRow> Alerts { get; } = new();

    public bool HasAlerts => Alerts.Count > 0;
    public bool ShowEmptyState => !IsLoading && !HasAlerts && !HasError;
    public bool ShowLoadingState => IsLoading && !HasAlerts;
    public bool ShowDetailPane => SelectedAlert is not null;

    public int UnreadCount {
        get {
            var count = 0;
            foreach (var row in Alerts) {
                if (!row.IsRead) count++;
            }
            return count;
        }
    }

    public AlertsTabViewModel(
        IDaemonClient daemonClient,
        DaemonStreamSubscriber streamSubscriber,
        IDispatcher dispatcher,
        INotificationService notifications,
        Func<string, Task>? navigateToFirewallRule = null,
        Func<string, bool>? fileExistsCheck = null
    ) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(streamSubscriber);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(notifications);
        _daemonClient = daemonClient;
        _streamSubscriber = streamSubscriber;
        _dispatcher = dispatcher;
        _notifications = notifications;
        _navigateToFirewallRule = navigateToFirewallRule;
        // Defaults to File.Exists for production; tests inject a controllable
        // predicate. Mirrors FirewallTabViewModel's _fileExistsCheck pattern.
        _fileExistsCheck = fileExistsCheck ?? File.Exists;

        _streamSubscriber.AlertReceived += OnAlertReceived;
        _streamSubscriber.RuleChangeReceived += OnRuleChange;
        Alerts.CollectionChanged += (_, _) => {
            OnPropertyChanged(nameof(HasAlerts));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(ShowLoadingState));
            OnPropertyChanged(nameof(UnreadCount));
        };
    }

    public void Dispose() {
        _streamSubscriber.AlertReceived -= OnAlertReceived;
        _streamSubscriber.RuleChangeReceived -= OnRuleChange;
        _activationCts?.Cancel();
        _activationCts?.Dispose();
    }

    /// <summary>
    /// Initial load. Idempotent — concurrent callers and post-completion
    /// callers all await the same underlying activation task so the data is
    /// guaranteed to be loaded by the time <c>await ActivateAsync</c>
    /// returns. Both the tab-switch (via <c>OnActiveTabChanged</c>'s fire-
    /// and-forget) and the deep-link (via <c>NavigateToAlertAsync</c>'s
    /// awaited call) hit this; the second caller hands back the in-flight
    /// task instead of returning a bogus completed task. Mirrors
    /// <see cref="FirewallTabViewModel.ActivateAsync"/>'s contract.
    /// </summary>
    public Task ActivateAsync(CancellationToken cancellationToken) {
        if (_activationTask is not null) return _activationTask;
        _activationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activationTask = LoadInitialDataAsync(_activationCts.Token);
        return _activationTask;
    }

    private async Task LoadInitialDataAsync(CancellationToken cancellationToken) {
        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try {
            var snapshot = await _daemonClient.GetSnapshotAsync(cancellationToken);
            // Seed the outbound-block cache from the snapshot's rule list
            // BEFORE building rows so each AlertRow lands with the right
            // IsOutboundBlocked value on first construction.
            foreach (var rule in snapshot.FirewallRules) {
                if (rule.Direction == Direction.Outbound && rule.Action == FirewallAction.Block) {
                    _outboundBlockedPaths.Add(rule.ProcessPath);
                }
            }
            foreach (var alert in snapshot.RecentAlerts) {
                var row = AlertRow.FromProto(alert);
                row.IsOutboundBlocked = _outboundBlockedPaths.Contains(row.ProcessPath);
                AppendUnique(row);
            }
            // Auto-select the newest alert so the detail pane has content
            // on first paint. Skipped if we landed on the empty state.
            if (Alerts.Count > 0) {
                SelectedAlert = Alerts[0];
            }
        } catch (OperationCanceledException) {
            // Tab disposed mid-load — drop silently.
        } catch (RpcException ex) {
            HasError = true;
            ErrorMessage = $"Failed to load alerts: {ex.Status.Detail}";
        } catch (Exception ex) {
            HasError = true;
            ErrorMessage = $"Failed to load alerts: {ex.Message}";
        } finally {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Live update: the daemon broadcasts an alert; insert at the top of the
    /// list (newest-first ordering) unless we've already seen this seq.
    /// </summary>
    private void OnAlertReceived(AlertEvent ev) {
        // The stream-subscriber thread fires this; marshal to the UI thread
        // before mutating the observable collection.
        _dispatcher.Post(() => {
            if (ev.Alert is null) return;
            var row = AlertRow.FromProto(ev.Alert);
            // Apply current outbound-block state so a brand-new alert for an
            // already-blocked process renders with the right button label.
            row.IsOutboundBlocked = _outboundBlockedPaths.Contains(row.ProcessPath);
            if (!AppendUnique(row, atFront: true)) return;
            // Auto-select the new alert if the user has nothing currently
            // selected (e.g., they just opened the tab to an empty state).
            // Otherwise leave selection alone — yanking it under the user's
            // focus would be hostile.
            SelectedAlert ??= row;
            // OS toast: title = AlertKind label, body = display name + summary
            // on a single line. Fires only on the live path (atFront=true);
            // historic alerts loaded via ActivateAsync use atFront=false and
            // never reach this code, so opening the tab doesn't flood the
            // user with a toast per row.
            _notifications.Notify(
                row.Seq, (CoreAlertKind)row.Kind, row.KindLabel,
                $"{row.DisplayName} — {row.Summary}");
        });
    }

    /// <summary>
    /// Selects the alert with <paramref name="seq"/> if it's in the currently-
    /// loaded list. No-op if not found (e.g., evicted by the 500-row retention
    /// cap, or the seq predates the snapshot's RecentAlertLimit). Used by the
    /// notification click deep-link path.
    /// </summary>
    public void SelectBySeq(long seq) {
        foreach (var row in Alerts) {
            if (row.Seq == seq) {
                SelectedAlert = row;
                return;
            }
        }
    }

    /// <summary>
    /// Live rule update: keep <see cref="_outboundBlockedPaths"/> in sync
    /// with the daemon and push the new state into every visible
    /// <see cref="AlertRow"/> that shares the rule's process path. Mirrors
    /// <c>FirewallTabViewModel.OnRuleChange</c> — same dispatcher hop, same
    /// change-kind switch, scoped to just the outbound flag the Alerts tab
    /// surfaces.
    /// </summary>
    private void OnRuleChange(FirewallRuleChange change) {
        _dispatcher.Post(() => {
            if (change.Rule is null) return;
            if (change.Rule.Direction != Direction.Outbound) return;
            var path = change.Rule.ProcessPath;
            var nowBlocked = change.Change != FirewallRuleChange.Types.ChangeKind.Removed
                          && change.Rule.Action == FirewallAction.Block;
            if (nowBlocked) _outboundBlockedPaths.Add(path);
            else _outboundBlockedPaths.Remove(path);
            foreach (var row in Alerts) {
                if (string.Equals(row.ProcessPath, path, StringComparison.OrdinalIgnoreCase))
                    row.IsOutboundBlocked = nowBlocked;
            }
        });
    }

    /// <summary>
    /// Inserts <paramref name="row"/> if its seq is new; returns whether it
    /// was actually added. Caps the list at <see cref="MaxRetainedAlerts"/>
    /// by evicting the oldest entry. Mirrors
    /// <c>FirewallActivityViewModel.AppendUnique</c>.
    /// </summary>
    private bool AppendUnique(AlertRow row, bool atFront = false) {
        // Hide alerts for non-targetable sentinel processes — the kernel
        // "System" pseudo-process and the "unknown" placeholder for PIDs that
        // couldn't be resolved. The daemon no longer emits these
        // (NewProcessDetector suppresses them), but pre-fix chains still hold
        // them; filtering at this single insertion choke point hides the
        // legacy rows without mutating the append-only chain — the same
        // exclusion the Firewall and Traffic tabs apply. Fully-qualified
        // because this file deliberately avoids `using Beholder.Core` (see the
        // header note on enum aliasing).
        if (Beholder.Core.ProcessSentinels.IsNonTargetable(row.ProcessPath)) return false;

        if (!_seenSeqs.Add(row.Seq)) return false;

        if (atFront) Alerts.Insert(0, row);
        else Alerts.Add(row);

        while (Alerts.Count > MaxRetainedAlerts) {
            var evicted = Alerts[^1];
            Alerts.RemoveAt(Alerts.Count - 1);
            _seenSeqs.Remove(evicted.Seq);
        }
        return true;
    }

    /// <summary>
    /// Optimistic mark-read on selection change: flip
    /// <see cref="AlertRow.IsRead"/> immediately, fire the RPC in the
    /// background, revert on RPC failure. Mirrors the pill-click pattern
    /// from <see cref="FirewallTabViewModel.CycleInPill"/>.
    /// </summary>
    partial void OnSelectedAlertChanged(AlertRow? value) {
        OnPropertyChanged(nameof(UnreadCount));
        if (value is null) return;
        // Refresh missing-file state on every selection — the binary may
        // have been deleted (or restored) since the row was last selected.
        // ChainError alerts have empty ProcessPath; treat them as "not
        // missing" so the (already-hidden by IsVisible) buttons stay in
        // their unreachable state without a stale flag. See Phase 6.10.
        value.IsExecutableMissing = !string.IsNullOrEmpty(value.ProcessPath)
                                  && !SafeFileExists(value.ProcessPath);
        if (value.IsRead) return;
        _ = MarkSelectedAsReadAsync(value);
    }

    /// <summary>
    /// Wraps the injected <see cref="_fileExistsCheck"/> in a try/catch so
    /// permission errors or malformed paths can't lock the user out of the
    /// action buttons. Defaults to "exists" on exception — the user can
    /// still attempt the firewall action, and the daemon will surface a
    /// real error if the path genuinely fails. Mirrors
    /// <c>FirewallTabViewModel.SafeFileExists</c>.
    /// </summary>
    private bool SafeFileExists(string path) {
        try { return _fileExistsCheck(path); }
        catch { return true; }
    }

    private async Task MarkSelectedAsReadAsync(AlertRow row) {
        // Clear any stale error from a prior action so a successful retry
        // implicitly dismisses it (see UI_DESIGN.md §5.10 auto-clear).
        ClearError();
        row.IsRead = true;
        OnPropertyChanged(nameof(UnreadCount));
        try {
            await _daemonClient.MarkAlertReadAsync(
                new MarkAlertReadRequest { Seq = row.Seq }, CancellationToken.None);
        } catch (Exception ex) {
            row.IsRead = false;
            OnPropertyChanged(nameof(UnreadCount));
            HasError = true;
            ErrorMessage = $"Failed to mark alert as read: {ex.Message}";
        }
    }

    /// <summary>
    /// Block the selected alert's process outbound. Failure surfaces an
    /// error banner; success is observable via the Firewall tab so this
    /// surface doesn't need its own confirmation. <see cref="AlertRow.IsOutboundBlocked"/>
    /// flips through <see cref="OnRuleChange"/> when the daemon broadcasts
    /// the new rule — single-writer keeps state consistent if a parallel
    /// toggle from the Firewall tab races us.
    /// </summary>
    [RelayCommand]
    private async Task BlockProcessOutAsync(AlertRow? row) {
        if (row is null || string.IsNullOrEmpty(row.ProcessPath)) return;
        ClearError();   // see UI_DESIGN.md §5.10 auto-clear
        try {
            await _daemonClient.ApplyFirewallRuleAsync(new ApplyFirewallRuleRequest {
                ProcessPath = row.ProcessPath,
                Direction = Direction.Outbound,
                Action = FirewallAction.Block,
                Source = RuleSource.Manual,
            }, CancellationToken.None);
        } catch (Exception ex) {
            HasError = true;
            ErrorMessage = $"Failed to block {row.DisplayName}: {ex.Message}";
        }
    }

    /// <summary>
    /// Remove the Outbound + Block rule for the selected alert's process.
    /// Inverse of <see cref="BlockProcessOutAsync"/> — clicking the button
    /// when the toggle is in its "blocked" state surfaces this command.
    /// State flip is driven by the daemon's <c>RuleChange</c> broadcast
    /// (see <see cref="OnRuleChange"/>), not local optimism.
    /// </summary>
    [RelayCommand]
    private async Task UnblockProcessOutAsync(AlertRow? row) {
        if (row is null || string.IsNullOrEmpty(row.ProcessPath)) return;
        ClearError();   // see UI_DESIGN.md §5.10 auto-clear
        try {
            await _daemonClient.RemoveFirewallRuleAsync(new RemoveFirewallRuleRequest {
                ProcessPath = row.ProcessPath,
                Direction = Direction.Outbound,
            }, CancellationToken.None);
        } catch (Exception ex) {
            HasError = true;
            ErrorMessage = $"Failed to unblock {row.DisplayName}: {ex.Message}";
        }
    }

    /// <summary>
    /// Clears the error banner. Bound to the close-X on the inline
    /// <see cref="Beholder.Ui.Controls.ErrorBanner"/>; also called by every
    /// action method on entry so a successful retry implicitly dismisses
    /// stale errors. See UI_DESIGN.md §5.10.
    /// </summary>
    [RelayCommand]
    private void DismissError() => ClearError();

    private void ClearError() {
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// Deep-link to the Firewall tab and surface the matching rule row.
    /// The <c>navigateToFirewallRule</c> delegate (supplied by
    /// <c>MainWindowViewModel</c>) handles the tab switch + scroll/highlight,
    /// awaiting the Firewall tab's <c>ActivateAsync</c> internally so a
    /// cold-start deep-link doesn't race against rule-list population. The
    /// generated command is named <c>AddRuleCommand</c> (the <c>Async</c>
    /// suffix is stripped by <c>[RelayCommand]</c>), so the existing AXAML
    /// binding continues to resolve unchanged.
    /// </summary>
    [RelayCommand]
    private async Task AddRuleAsync(AlertRow? row) {
        if (row is null || string.IsNullOrEmpty(row.ProcessPath)) return;
        if (_navigateToFirewallRule is null) return;
        await _navigateToFirewallRule(row.ProcessPath);
    }
}
