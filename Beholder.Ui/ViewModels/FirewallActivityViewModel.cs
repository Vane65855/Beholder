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

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Backs the recent firewall activity strip at the bottom of the Firewall
/// tab. Loads the most recent <c>InitialFetchLimit</c> events on activation
/// and prepends live broadcasts as they arrive. The list is capped at
/// <c>MaxRetainedEvents</c> so a long-running session can't grow the
/// activity list unbounded — the daemon's chain remains the authoritative,
/// uncapped source of truth.
/// </summary>
internal sealed partial class FirewallActivityViewModel : ViewModelBase, IDisposable {
    /// <summary>
    /// How many events to fetch on tab activation. Matches the activity
    /// strip's reasonable visual ceiling — a 30-day exhaustive log is
    /// excessive for a strip that lives below the rule table.
    /// </summary>
    private const int InitialFetchLimit = 100;

    /// <summary>
    /// Live cap for the in-memory list. New events past this cap evict the
    /// oldest entry. Matches the plan target.
    /// </summary>
    private const int MaxRetainedEvents = 500;

    private readonly IDaemonClient _daemonClient;
    private readonly DaemonStreamSubscriber _streamSubscriber;
    private readonly IDispatcher _dispatcher;
    private readonly HashSet<long> _seenSeqs = new();

    private CancellationTokenSource? _activationCts;
    private bool _activated;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public ObservableCollection<FirewallActivityRow> Events { get; } = new();

    public bool HasEvents => Events.Count > 0;
    public bool ShowEmptyState => !IsLoading && !HasEvents && !HasError;

    public FirewallActivityViewModel(
        IDaemonClient daemonClient,
        DaemonStreamSubscriber streamSubscriber,
        IDispatcher dispatcher
    ) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(streamSubscriber);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _daemonClient = daemonClient;
        _streamSubscriber = streamSubscriber;
        _dispatcher = dispatcher;
        _streamSubscriber.RuleChangeReceived += OnRuleChange;
        Events.CollectionChanged += (_, _) => {
            OnPropertyChanged(nameof(HasEvents));
            OnPropertyChanged(nameof(ShowEmptyState));
        };
    }

    public void Dispose() {
        _streamSubscriber.RuleChangeReceived -= OnRuleChange;
        _activationCts?.Cancel();
        _activationCts?.Dispose();
    }

    public async Task ActivateAsync(CancellationToken cancellationToken) {
        if (_activated) return;
        _activated = true;
        _activationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try {
            var response = await _daemonClient.GetFirewallActivityAsync(
                new GetFirewallActivityRequest { Limit = InitialFetchLimit },
                _activationCts.Token);
            foreach (var ev in response.Events) {
                AppendUnique(FirewallActivityRow.FromProto(ev));
            }
        } catch (OperationCanceledException) {
            // Tab disposed mid-fetch — drop silently.
        } catch (RpcException ex) {
            HasError = true;
            ErrorMessage = $"Failed to load firewall activity: {ex.Status.Detail}";
        } catch (Exception ex) {
            HasError = true;
            ErrorMessage = $"Failed to load firewall activity: {ex.Message}";
        } finally {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    /// <summary>
    /// Live append: when the daemon broadcasts a rule change, we don't have
    /// the chain seq on the wire (the broadcast carries the rule itself, not
    /// the chain entry). Synthesize a row using <c>DateTimeOffset.UtcNow</c>
    /// + a synthetic negative seq so dedup against the initial fetch still
    /// works. The next tab re-activation reloads from the daemon and replaces
    /// the synthetic rows with their real chain entries.
    /// </summary>
    private void OnRuleChange(FirewallRuleChange change) {
        _dispatcher.Post(() => {
            var kind = change.Change switch {
                FirewallRuleChange.Types.ChangeKind.Created => FirewallActivityKind.RuleCreated,
                FirewallRuleChange.Types.ChangeKind.Changed => FirewallActivityKind.RuleChanged,
                FirewallRuleChange.Types.ChangeKind.Removed => FirewallActivityKind.RuleRemoved,
                _ => FirewallActivityKind.FirewallActivityUnknown,
            };
            var ev = new FirewallActivityEvent {
                // Negative synthetic seq — guaranteed not to collide with any
                // real chain row.
                Seq = -DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind = kind,
                TimestampUnixNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L,
                ProcessPath = change.Rule.ProcessPath,
                Direction = change.Rule.Direction,
                Action = change.Rule.Action,
                Source = change.Rule.Source,
            };
            AppendUnique(FirewallActivityRow.FromProto(ev), atFront: true);
        });
    }

    /// <summary>
    /// Clears the error banner. Bound to the close-X on the inline
    /// <see cref="Beholder.Ui.Controls.ErrorBanner"/>. See UI_DESIGN.md §5.10.
    /// </summary>
    [RelayCommand]
    private void DismissError() {
        HasError = false;
        ErrorMessage = string.Empty;
    }

    private void AppendUnique(FirewallActivityRow row, bool atFront = false) {
        // Skip exact-seq duplicates (e.g., two activations of the tab in the
        // same session, or a live broadcast racing the initial fetch).
        // Synthetic negative seqs are always unique because they're stamped
        // from milliseconds-since-epoch.
        if (!_seenSeqs.Add(row.Seq)) return;

        if (atFront) Events.Insert(0, row);
        else Events.Add(row);

        // Cap. Trim from the bottom (oldest end) — the freshest events are
        // always at the top by construction.
        while (Events.Count > MaxRetainedEvents) {
            var evicted = Events[^1];
            Events.RemoveAt(Events.Count - 1);
            _seenSeqs.Remove(evicted.Seq);
        }
    }
}

/// <summary>
/// Row VM for one entry in the activity strip. Pre-formats every column
/// at construction so the view template can stay shallow.
/// </summary>
internal sealed class FirewallActivityRow {
    public long Seq { get; }
    public DateTimeOffset Timestamp { get; }
    public string TimestampLabel { get; }
    public string KindLabel { get; }
    public string KindBadgeClass { get; }
    public string Description { get; }

    private FirewallActivityRow(
        long seq, DateTimeOffset timestamp, string timestampLabel,
        string kindLabel, string kindBadgeClass, string description
    ) {
        Seq = seq;
        Timestamp = timestamp;
        TimestampLabel = timestampLabel;
        KindLabel = kindLabel;
        KindBadgeClass = kindBadgeClass;
        Description = description;
    }

    public static FirewallActivityRow FromProto(FirewallActivityEvent ev) {
        ArgumentNullException.ThrowIfNull(ev);
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(ev.TimestampUnixNs / 1_000_000L).ToLocalTime();
        var (label, badgeClass, description) = ev.Kind switch {
            FirewallActivityKind.RuleCreated => (
                "RULE", "info",
                $"created · {ExtractName(ev.ProcessPath)} · {DirectionLabel(ev.Direction)} {ActionLabel(ev.Action)}"),
            FirewallActivityKind.RuleChanged => (
                "RULE", "info",
                $"changed · {ExtractName(ev.ProcessPath)} · {DirectionLabel(ev.Direction)} {ActionLabel(ev.Action)}"),
            FirewallActivityKind.RuleRemoved => (
                "RULE", "muted",
                $"removed · {ExtractName(ev.ProcessPath)} · {DirectionLabel(ev.Direction)}"),
            FirewallActivityKind.EnforcementToggled => (
                "ENFORCE", ev.EnforcementEnabled ? "info" : "danger",
                $"firewall enforcement: {(ev.EnforcementEnabled ? "ON" : "OFF")}"),
            _ => ("?", "muted", "unknown event"),
        };
        return new FirewallActivityRow(
            seq: ev.Seq,
            timestamp: ts,
            timestampLabel: ts.ToString("HH:mm:ss"),
            kindLabel: label,
            kindBadgeClass: badgeClass,
            description: description);
    }

    private static string ExtractName(string path) {
        if (string.IsNullOrEmpty(path)) return "—";
        try {
            var name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path : name;
        } catch (ArgumentException) {
            return path;
        }
    }

    private static string DirectionLabel(Direction dir) => dir switch {
        Direction.Inbound => "in",
        Direction.Outbound => "out",
        _ => "?",
    };

    private static string ActionLabel(FirewallAction action) => action switch {
        FirewallAction.Allow => "allow",
        FirewallAction.Block => "block",
        _ => "?",
    };
}
