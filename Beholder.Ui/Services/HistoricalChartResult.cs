using System.Collections.Generic;
using Beholder.Protocol.Local;

namespace Beholder.Ui.Services;

/// <summary>
/// Result of <see cref="HistoricalChartLoader.LoadProcessChartAsync"/>.
/// Per-process queries only build chart data — the sidebar stays on whatever
/// the last range load produced.
/// </summary>
internal sealed record HistoricalChartResult(
    IReadOnlyList<TrafficTimePoint> Points,
    long ResolutionMs);
