using System.Collections.Generic;
using Beholder.Protocol.Local;

namespace Beholder.Ui.Services;

/// <summary>
/// Result of <see cref="HistoricalChartLoader.LoadRangeAsync"/>. Carries both
/// the aggregate timeline and the per-process summaries so the VM can render
/// the chart and sidebar from a single load.
/// </summary>
/// <param name="Points">
/// Empty when the daemon has no data in the requested range. Callers should
/// check this before accessing <paramref name="Summaries"/>, which will also
/// be empty in that case (loader skips the second RPC).
/// </param>
internal sealed record HistoricalRangeResult(
    IReadOnlyList<TrafficTimePoint> Points,
    IReadOnlyList<ProcessTrafficSummaryProto> Summaries,
    long ResolutionMs);
