using System;
using System.IO;
using Beholder.Protocol.Local;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// One row in the Alerts tab's master list. Joins a chain-row alert
/// (delivered via <c>GetSnapshotAsync.RecentAlerts</c> at activation, or via
/// the live <c>DaemonStreamSubscriber.AlertReceived</c> event afterwards)
/// with the optimistic-UI read-state.
/// </summary>
/// <remarks>
/// Most fields are immutable — alerts are append-only chain rows, so
/// <c>Seq</c>, <c>Kind</c>, <c>ProcessPath</c>, <c>Summary</c>, and
/// <c>Timestamp</c> can never change after construction. Only <c>IsRead</c>
/// is observable, flipped when the user views the alert (optimistic) and
/// confirmed via the <c>MarkAlertRead</c> RPC.
/// </remarks>
internal sealed partial class AlertRow : ObservableObject {
    public long Seq { get; }
    public AlertKind Kind { get; }
    public string ProcessPath { get; }
    public string DisplayName { get; }
    public string Summary { get; }
    public DateTimeOffset Timestamp { get; }
    public string TimestampLabel { get; }

    [ObservableProperty]
    private bool _isRead;

    /// <summary>
    /// Whether an Outbound + Block firewall rule currently exists for this
    /// alert's process path. Drives the detail-pane footer's BLOCK / UNBLOCK
    /// toggle. Owned by <see cref="AlertsTabViewModel"/> — the single writer
    /// is the daemon's <c>RuleChange</c> broadcast (snapshot-seed at
    /// activation, live updates afterwards). Multiple alerts can share a
    /// process path; each row carries its own copy so the bindings work
    /// without a converter.
    /// </summary>
    [ObservableProperty]
    private bool _isOutboundBlocked;

    /// <summary>
    /// Whether the binary at <see cref="ProcessPath"/> no longer exists on
    /// disk. Owned by <see cref="AlertsTabViewModel"/> — checked on every
    /// selection (via <c>OnSelectedAlertChanged</c>). When true, the detail
    /// pane disables the action buttons because they'd produce a useless
    /// firewall rule against a path no process can occupy. See Phase 6.10.
    /// </summary>
    [ObservableProperty]
    private bool _isExecutableMissing;

    /// <summary>
    /// Uppercase label for the master-list kind badge. Matches the project's
    /// all-caps convention for compact data labels (Phase 6.4 SOURCE column,
    /// firewall activity strip kind labels). Em-dash for any future enum
    /// variant the UI doesn't recognize.
    /// </summary>
    public string KindLabel => Kind switch {
        AlertKind.NewProcess => "NEW PROCESS",
        AlertKind.HashChanged => "HASH CHANGED",
        AlertKind.ChainError => "CHAIN ERROR",
        _ => "—",
    };

    /// <summary>
    /// Visual class hook driving the kind-badge color. Mirrors the Phase 6.5
    /// activity strip's <c>info</c>/<c>warn</c>/<c>danger</c>/<c>muted</c>
    /// vocabulary so the two surfaces feel coherent.
    /// </summary>
    public string KindBadgeClass => Kind switch {
        AlertKind.NewProcess => "info",
        AlertKind.HashChanged => "warn",
        AlertKind.ChainError => "danger",
        _ => "muted",
    };

    private AlertRow(
        long seq,
        AlertKind kind,
        string processPath,
        string summary,
        DateTimeOffset timestamp,
        bool isRead
    ) {
        Seq = seq;
        Kind = kind;
        ProcessPath = processPath;
        Summary = summary;
        Timestamp = timestamp;
        DisplayName = ExtractDisplayName(processPath);
        TimestampLabel = timestamp.ToLocalTime().ToString("HH:mm:ss");
        _isRead = isRead;
    }

    /// <summary>
    /// Builds a row from the wire shape (used by both the snapshot-fetch and
    /// live-broadcast paths — they both produce <see cref="Alert"/> protos).
    /// </summary>
    public static AlertRow FromProto(Alert alert) {
        ArgumentNullException.ThrowIfNull(alert);
        return new AlertRow(
            seq: alert.Seq,
            kind: alert.Kind,
            processPath: alert.ProcessPath,
            summary: alert.Summary,
            timestamp: DateTimeOffset.FromUnixTimeMilliseconds(alert.TimestampUnixNs / 1_000_000L),
            // FirstViewedAtUnixNs == 0 is the proto sentinel for "unread"
            // (see beholder_local.proto field comment at line 66–67).
            isRead: alert.FirstViewedAtUnixNs != 0L);
    }

    /// <summary>
    /// Trims to filename for the master-list display column. Falls back to
    /// the full path if extraction throws (malformed path) so the user
    /// always sees something. ChainError alerts have an empty
    /// <c>ProcessPath</c> by definition; render those as the em-dash so the
    /// list layout doesn't collapse to a blank column.
    /// </summary>
    private static string ExtractDisplayName(string processPath) {
        if (string.IsNullOrEmpty(processPath)) return "—";
        try {
            var name = Path.GetFileName(processPath);
            return string.IsNullOrEmpty(name) ? processPath : name;
        } catch (ArgumentException) {
            return processPath;
        }
    }
}
