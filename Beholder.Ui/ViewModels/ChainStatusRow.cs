using System;
using System.Globalization;
using Beholder.Protocol.Local;
using Beholder.Ui.Converters;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Cached chain-verification snapshot displayed in the Settings tab's
/// Maintenance section: "Last verified: 3m ago — valid (1,247 rows)" /
/// "Last verified: 8s ago — failed at seq 482: hash mismatch" / "Never
/// verified this session". Updated by both the periodic
/// <c>ChainIntegrityMonitor</c> (surfaced via the next
/// <c>GetStorageStats</c> refresh) and the user-triggered "Verify chain
/// integrity now" button (surfaced directly from the <c>VerifyChain</c>
/// RPC response).
/// </summary>
/// <remarks>
/// Observable so the parent VM's 1-second relative-time ticker can refresh
/// <see cref="LastVerifiedAtLabel"/> without rebuilding the row. Mirrors the
/// <see cref="LanDeviceRow"/> precedent.
/// </remarks>
internal sealed partial class ChainStatusRow : ObservableObject {
    /// <summary>
    /// Placeholder rendered when no verification has run yet this session —
    /// fresh daemon start where the periodic monitor hasn't fired and the
    /// user hasn't clicked "Verify chain integrity now" either.
    /// </summary>
    public const string NeverVerifiedLabel = "Never verified this session";

    public DateTimeOffset? LastVerifiedAt { get; private set; }
    public bool HasResult { get; private set; }
    public bool IsValid { get; private set; }
    public long RowsVerified { get; private set; }
    public long? FailedAtSeq { get; private set; }
    public string? ErrorMessage { get; private set; }

    [ObservableProperty]
    private string _lastVerifiedAtLabel = NeverVerifiedLabel;

    [ObservableProperty]
    private string _resultSummary = string.Empty;

    /// <summary>
    /// Theme-token key for the status pill's background brush:
    /// <c>"SeveritySuccess"</c> when the chain is valid,
    /// <c>"SeverityDanger"</c> when it failed, <c>"BorderStrong"</c> when
    /// no verification has run yet. XAML binds to this via a
    /// <c>DynamicResource</c>-shaped converter so the pill color reacts to
    /// theme swaps without rebinding.
    /// </summary>
    [ObservableProperty]
    private string _statusPillBrushKey = "BorderStrong";

    /// <summary>
    /// Short pill label: <c>"VALID"</c> / <c>"INVALID"</c> / <c>"NEVER VERIFIED"</c>.
    /// </summary>
    [ObservableProperty]
    private string _statusPillLabel = "NEVER VERIFIED";

    /// <summary>
    /// Builds a row from a wire <see cref="ChainStatus"/> snapshot. Pass
    /// <c>null</c> to render the "never verified" placeholder (used when
    /// <c>GetStorageStatsResponse.HasChainStatus == false</c>).
    /// </summary>
    public static ChainStatusRow FromProto(ChainStatus? proto, TimeProvider timeProvider) {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var row = new ChainStatusRow();
        row.UpdateFromProto(proto, timeProvider);
        return row;
    }

    /// <summary>
    /// Overwrites the row in place. Used both by the periodic refresh
    /// (re-fetched <c>GetStorageStats</c>) and the user-triggered verify
    /// (the <c>VerifyChain</c> RPC's response is translated into a
    /// <see cref="ChainStatus"/>-shaped update right here in the VM).
    /// </summary>
    public void UpdateFromProto(ChainStatus? proto, TimeProvider timeProvider) {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (proto is null) {
            LastVerifiedAt = null;
            HasResult = false;
            IsValid = false;
            RowsVerified = 0;
            FailedAtSeq = null;
            ErrorMessage = null;
            LastVerifiedAtLabel = NeverVerifiedLabel;
            ResultSummary = string.Empty;
            StatusPillBrushKey = "BorderStrong";
            StatusPillLabel = "NEVER VERIFIED";
            return;
        }
        LastVerifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(proto.LastVerifiedUnixNs / 1_000_000L);
        HasResult = true;
        IsValid = proto.IsValid;
        RowsVerified = proto.RowsVerified;
        FailedAtSeq = proto.FailedAtSeq > 0 ? proto.FailedAtSeq : null;
        ErrorMessage = string.IsNullOrEmpty(proto.ErrorMessage) ? null : proto.ErrorMessage;
        RefreshRelativeLabel(timeProvider);
        ResultSummary = IsValid
            ? $"{RowsVerified.ToString("N0", CultureInfo.InvariantCulture)} rows"
            : FailedAtSeq.HasValue
                ? $"failed at seq {FailedAtSeq.Value}: {ErrorMessage}"
                : $"failed: {ErrorMessage}";
        StatusPillBrushKey = IsValid ? "SeveritySuccess" : "SeverityDanger";
        StatusPillLabel = IsValid ? "VALID" : "INVALID";
    }

    /// <summary>
    /// Re-formats <see cref="LastVerifiedAtLabel"/> against the current
    /// clock. Called by the parent VM's 1-second ticker so "5s ago" ticks
    /// up to "1m ago" without re-fetching.
    /// </summary>
    public void RefreshRelativeLabel(TimeProvider timeProvider) {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (LastVerifiedAt is null) {
            LastVerifiedAtLabel = NeverVerifiedLabel;
            return;
        }
        LastVerifiedAtLabel = RelativeTimeAgoConverter.Format(
            LastVerifiedAt.Value, timeProvider.GetUtcNow());
    }
}
