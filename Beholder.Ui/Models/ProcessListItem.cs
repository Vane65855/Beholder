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
    private long _totalBytesOut;

    [ObservableProperty]
    private string _totalBytesLabel = "0 B";

    public ProcessListItem(string processPath, string displayName, bool isAll = false) {
        ProcessPath = processPath;
        DisplayName = displayName;
        IsAll = isAll;
        SeriesIndex = isAll ? 1 : SeriesColorHelper.GetSeriesIndex(processPath);
    }

    public void UpdateTraffic(long totalBytesOut) {
        TotalBytesOut = totalBytesOut;
        TotalBytesLabel = ByteFormatter.FormatBytes(totalBytesOut);
    }
}
