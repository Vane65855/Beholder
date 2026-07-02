using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Beholder.Protocol.Local;

namespace Beholder.Ui.Services;

/// <summary>
/// UI-side mirror of the daemon's Traffic Totals exclusion list ("Exclude from
/// totals") plus the UI-local "show excluded processes" display preference.
/// The live views consult it when aggregating (status-strip totals, the
/// All-processes chart, the process list's all-row) and when deciding whether
/// an excluded row is hidden or shown with a marker.
/// </summary>
/// <remarks>
/// <para>The daemon owns the list (chain-audited, enforced in its aggregate
/// SQL); this mirror exists because live-mode aggregation happens client-side.
/// It stays trustworthy without a broadcast because this single-instance UI is
/// the list's only writer: it seeds from <c>GetSettings</c> on daemon connect
/// and the Settings tab updates it with every echoed <c>SetTotalsSettings</c>
/// response (ADR 010 accepts settings changes without a broadcast surface).</para>
/// <para><see cref="Changed"/> may fire on a background thread (the
/// connect-time seed); subscribers marshal to the UI thread themselves.</para>
/// </remarks>
internal sealed class TotalsExclusionUiState {
    private readonly object _gate = new();
    private HashSet<string> _pathSet = new(StringComparer.OrdinalIgnoreCase);
    private string[] _paths = [];
    private bool _showExcluded;

    /// <summary>The current excluded process paths, in list order (immutable snapshot).</summary>
    public IReadOnlyList<string> ExcludedProcessPaths {
        get { lock (_gate) return _paths; }
    }

    /// <summary>
    /// When true, excluded processes stay in the Traffic tab's process list
    /// with a marker; when false their rows are hidden. Never affects the
    /// totals math — excluded processes are skipped from aggregates either way.
    /// </summary>
    public bool ShowExcluded {
        get { lock (_gate) return _showExcluded; }
    }

    /// <summary>True when <paramref name="processPath"/> is excluded (ordinal case-insensitive).</summary>
    public bool IsExcluded(string processPath) {
        if (string.IsNullOrEmpty(processPath)) return false;
        lock (_gate) return _pathSet.Contains(processPath);
    }

    /// <summary>Fires after the exclusion list or the show-preference actually changes.</summary>
    public event Action? Changed;

    /// <summary>Replaces the exclusion list; fires <see cref="Changed"/> only on a real transition.</summary>
    public void SetExcludedPaths(IReadOnlyList<string> paths) {
        ArgumentNullException.ThrowIfNull(paths);
        var newSet = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        bool changed;
        lock (_gate) {
            changed = !_pathSet.SetEquals(newSet);
            _pathSet = newSet;
            _paths = [.. paths];
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Sets the show-excluded display preference; fires <see cref="Changed"/> only on a flip.</summary>
    public void SetShowExcluded(bool showExcluded) {
        bool changed;
        lock (_gate) {
            changed = _showExcluded != showExcluded;
            _showExcluded = showExcluded;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Best-effort re-seed from the daemon's <c>GetSettings</c>, called on
    /// every daemon (re)connect. Failures are swallowed — the mirror keeps its
    /// last-known list and the next connect retries — mirroring
    /// <c>ProcessStateService.SeedAsync</c>'s posture for the same event.
    /// </summary>
    public async Task RefreshFromDaemonAsync(IDaemonClient daemonClient, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        try {
            var settings = await daemonClient.GetSettingsAsync(new GetSettingsRequest(), cancellationToken);
            if (settings.Totals is not null) SetExcludedPaths([.. settings.Totals.ExcludedProcessPaths]);
        } catch (Exception) {
            // Best-effort: seed retries on the next connect.
        }
    }
}
