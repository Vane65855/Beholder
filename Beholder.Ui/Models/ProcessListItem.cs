using Beholder.Ui.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.Models;

/// <summary>
/// Represents a single row in the Traffic tab's process list.
/// The "All processes" entry uses <see cref="IsAll"/> = true and is pinned at the top.
/// </summary>
internal sealed partial class ProcessListItem : ObservableObject {
    public string ProcessPath { get; }
    public string DisplayName { get; }
    public bool IsAll { get; }

    /// <summary>
    /// Series color index (1-12) resolved from the process path hash.
    /// Used by <c>SeriesIndexToBrushConverter</c> to display the colored dot.
    /// </summary>
    public int SeriesIndex { get; }

    [ObservableProperty]
    private long _recentBytesIn;

    [ObservableProperty]
    private long _recentBytesOut;

    [ObservableProperty]
    private string _recentInLabel = "0 B";

    [ObservableProperty]
    private string _recentOutLabel = "0 B";

    /// <summary>
    /// Combined recent in+out traffic — used as the sort key for the process list.
    /// Derived, not observable; callers read it after <see cref="UpdateTraffic"/>.
    /// </summary>
    public long SortKey => RecentBytesIn + RecentBytesOut;

    public ProcessListItem(string processPath, string displayName, bool isAll = false) {
        ProcessPath = processPath;
        DisplayName = displayName;
        IsAll = isAll;
        SeriesIndex = isAll ? 1 : SeriesColorHelper.GetSeriesIndex(processPath);
    }

    public void UpdateTraffic(long recentBytesIn, long recentBytesOut) {
        RecentBytesIn = recentBytesIn;
        RecentBytesOut = recentBytesOut;
        RecentInLabel = ByteFormatter.FormatBytes(recentBytesIn);
        RecentOutLabel = ByteFormatter.FormatBytes(recentBytesOut);
    }
}
