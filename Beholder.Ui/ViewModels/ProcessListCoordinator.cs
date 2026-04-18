using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Beholder.Protocol.Local;
using Beholder.Ui.Models;
using Beholder.Ui.Services;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Owns the Traffic tab's process list — an <see cref="ObservableCollection{T}"/>
/// of <see cref="ProcessListItem"/> with a pinned "All processes" aggregate row
/// at index 0. Handles upsert-from-live-states, historical-summaries rebuild,
/// idle-process removal, and Move-based resort that preserves ListBox selection
/// identity. Extracted from <c>TrafficTabViewModel</c> to keep the VM focused on
/// observable state and event routing; the coordinator is the authority for
/// list mutation.
/// </summary>
/// <remarks>
/// Thread safety: all mutations must happen on the UI thread — the
/// <see cref="List"/> is bound to an Avalonia <c>ListBox</c> and any background-
/// thread mutation would crash the XAML binding. The VM dispatches to the UI
/// thread before calling into this class.
/// </remarks>
internal sealed class ProcessListCoordinator {
    private readonly Dictionary<string, ProcessListItem> _lookup =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Observable process list, bound to the sidebar ListBox.
    /// Index 0 is always <see cref="AllProcessesItem"/>; subsequent indices
    /// hold real-process items sorted by recent traffic descending.
    /// </summary>
    public ObservableCollection<ProcessListItem> List { get; } = [];

    /// <summary>
    /// The pinned aggregate row. Selection falls back to this item whenever
    /// the user's prior selection is removed (e.g., goes idle in live mode).
    /// </summary>
    public ProcessListItem AllProcessesItem { get; }

    public ProcessListCoordinator() {
        AllProcessesItem = new ProcessListItem(string.Empty, "All processes", isAll: true);
        List.Add(AllProcessesItem);
    }

    /// <summary>
    /// Upserts items from the live process-states dictionary using each
    /// process's 5-minute recent-window sum as the display metric + sort key.
    /// Processes whose recent window has gone to zero are removed. Updates
    /// the pinned all-row with the total across all seen processes.
    /// </summary>
    public void Upsert(IReadOnlyDictionary<string, ProcessState> states) {
        ArgumentNullException.ThrowIfNull(states);

        long allRecentIn = 0;
        long allRecentOut = 0;
        foreach (var kvp in states) {
            var state = kvp.Value;
            long recentIn = 0;
            long recentOut = 0;
            for (var i = 0; i < state.RecentDeltaIn.Count; i++)
                recentIn += state.RecentDeltaIn[i];
            for (var i = 0; i < state.RecentDeltaOut.Count; i++)
                recentOut += state.RecentDeltaOut[i];

            allRecentIn += recentIn;
            allRecentOut += recentOut;

            if (recentIn + recentOut == 0) {
                Remove(kvp.Key);
                continue;
            }

            if (!_lookup.TryGetValue(kvp.Key, out var item)) {
                item = new ProcessListItem(kvp.Key, state.DisplayName);
                _lookup[kvp.Key] = item;
                List.Add(item);
            }
            item.UpdateTraffic(recentIn, recentOut);
        }

        AllProcessesItem.UpdateTraffic(allRecentIn, allRecentOut);
    }

    /// <summary>
    /// Sorts items 1..N by recent traffic descending using <see cref="ObservableCollection{T}.Move"/>
    /// only. Indexer assignment (<c>List[i] = x</c>) fires <c>NotifyCollection-
    /// ChangedAction.Replace</c>, which Avalonia's <c>ListBox</c> handles by
    /// clearing its <c>SelectedItem</c> if the replaced index held the user's
    /// current selection. Move preserves identity so selection follows the
    /// moved item. Index 0 (the pinned all-row) is never moved.
    /// </summary>
    public void Sort() {
        if (List.Count <= 2) return;

        var sorted = new List<ProcessListItem>(List.Count - 1);
        for (var i = 1; i < List.Count; i++)
            sorted.Add(List[i]);
        sorted.Sort(static (a, b) => b.SortKey.CompareTo(a.SortKey));

        for (var targetIndex = 1; targetIndex <= sorted.Count; targetIndex++) {
            var desired = sorted[targetIndex - 1];
            var currentIndex = List.IndexOf(desired);
            if (currentIndex != targetIndex)
                List.Move(currentIndex, targetIndex);
        }
    }

    /// <summary>
    /// Removes all process items except the pinned all-row. Fires a single
    /// <c>CollectionChanged(Reset)</c> followed by one <c>Add</c> rather than
    /// N individual removals — lighter UI invalidation on range transitions.
    /// </summary>
    public void Clear() {
        _lookup.Clear();
        if (List.Count <= 1) return;
        var all = List[0];
        List.Clear();
        List.Add(all);
    }

    /// <summary>
    /// Rebuilds the list from a historical <c>GetProcessSummaries</c> response.
    /// Used only by range-change loads; per-process chart queries leave the
    /// list untouched.
    /// </summary>
    public void ApplyHistorical(IReadOnlyList<ProcessTrafficSummaryProto> summaries) {
        ArgumentNullException.ThrowIfNull(summaries);
        Clear();

        long allHistIn = 0;
        long allHistOut = 0;
        foreach (var summary in summaries) {
            allHistIn += summary.TotalBytesIn;
            allHistOut += summary.TotalBytesOut;
            var item = new ProcessListItem(summary.ProcessPath, summary.ProcessName);
            item.UpdateTraffic(summary.TotalBytesIn, summary.TotalBytesOut);
            _lookup[summary.ProcessPath] = item;
            List.Add(item);
        }

        AllProcessesItem.UpdateTraffic(allHistIn, allHistOut);
        Sort();
    }

    private void Remove(string processPath) {
        if (_lookup.Remove(processPath, out var item))
            List.Remove(item);
    }
}
