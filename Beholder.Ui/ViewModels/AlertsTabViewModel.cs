using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;

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
    private readonly Action<string>? _navigateToFirewallRule;
    private readonly HashSet<long> _seenSeqs = new();

    private CancellationTokenSource? _activationCts;
    private bool _activated;

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
        Action<string>? navigateToFirewallRule = null
    ) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(streamSubscriber);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _daemonClient = daemonClient;
        _streamSubscriber = streamSubscriber;
        _dispatcher = dispatcher;
        _navigateToFirewallRule = navigateToFirewallRule;

        _streamSubscriber.AlertReceived += OnAlertReceived;
        Alerts.CollectionChanged += (_, _) => {
            OnPropertyChanged(nameof(HasAlerts));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(ShowLoadingState));
            OnPropertyChanged(nameof(UnreadCount));
        };
    }

    public void Dispose() {
        _streamSubscriber.AlertReceived -= OnAlertReceived;
        _activationCts?.Cancel();
        _activationCts?.Dispose();
    }

    /// <summary>
    /// Initial load. Idempotent — a second call short-circuits so repeated
    /// tab switches don't re-fetch. Mirrors
    /// <see cref="FirewallTabViewModel.ActivateAsync"/>'s contract.
    /// </summary>
    public async Task ActivateAsync(CancellationToken cancellationToken) {
        if (_activated) return;
        _activated = true;
        _activationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try {
            var snapshot = await _daemonClient.GetSnapshotAsync(_activationCts.Token);
            foreach (var alert in snapshot.RecentAlerts) {
                AppendUnique(AlertRow.FromProto(alert));
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
            if (!AppendUnique(row, atFront: true)) return;
            // Auto-select the new alert if the user has nothing currently
            // selected (e.g., they just opened the tab to an empty state).
            // Otherwise leave selection alone — yanking it under the user's
            // focus would be hostile.
            SelectedAlert ??= row;
        });
    }

    /// <summary>
    /// Inserts <paramref name="row"/> if its seq is new; returns whether it
    /// was actually added. Caps the list at <see cref="MaxRetainedAlerts"/>
    /// by evicting the oldest entry. Mirrors
    /// <c>FirewallActivityViewModel.AppendUnique</c>.
    /// </summary>
    private bool AppendUnique(AlertRow row, bool atFront = false) {
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
        if (value is null || value.IsRead) return;
        _ = MarkSelectedAsReadAsync(value);
    }

    private async Task MarkSelectedAsReadAsync(AlertRow row) {
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
    /// surface doesn't need its own confirmation.
    /// </summary>
    [RelayCommand]
    private async Task BlockProcessOutAsync(AlertRow? row) {
        if (row is null || string.IsNullOrEmpty(row.ProcessPath)) return;
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
    /// Deep-link to the Firewall tab and surface the matching rule row.
    /// The <c>navigateToFirewallRule</c> delegate (supplied by
    /// <c>MainWindowViewModel</c>) handles the tab switch + scroll/highlight.
    /// </summary>
    [RelayCommand]
    private void AddRule(AlertRow? row) {
        if (row is null || string.IsNullOrEmpty(row.ProcessPath)) return;
        _navigateToFirewallRule?.Invoke(row.ProcessPath);
    }
}
