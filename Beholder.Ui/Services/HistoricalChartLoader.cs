using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Beholder.Protocol.Local;
using Beholder.Ui.Models;

namespace Beholder.Ui.Services;

/// <summary>
/// Owns the daemon I/O for historical chart queries. Extracts the request-
/// construction, resolution-computation, and RPC-sequencing concerns out of
/// <see cref="Beholder.Ui.ViewModels.TrafficTabViewModel"/> so the VM keeps
/// state orchestration but stops mixing network I/O with presentation logic.
/// </summary>
internal sealed class HistoricalChartLoader {
    /// <summary>
    /// Target number of output buckets per chart. The UI-side resolution hint
    /// is the requested range divided by this target; the daemon treats it as
    /// advisory and derives the real bucket width from actual data extent.
    /// 300 was picked to comfortably fill a 1280+ px wide chart with visible
    /// buckets.
    /// </summary>
    private const int TargetOutputBuckets = 300;

    /// <summary>
    /// Floor on the computed resolution hint. Degenerate small ranges (e.g.,
    /// a 30-second custom pick) would otherwise request sub-second resolution,
    /// which no tier serves — 1s is the finest tier's native width.
    /// </summary>
    private const long MinResolutionMs = 1000;

    private readonly IDaemonClient _daemonClient;

    public HistoricalChartLoader(IDaemonClient daemonClient) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        _daemonClient = daemonClient;
    }

    /// <summary>
    /// Loads a full-range historical view: aggregate timeline + per-process
    /// summaries. Skips the summaries RPC when the timeline is empty so the
    /// caller's empty-state rendering doesn't burn a second round-trip.
    /// </summary>
    public async Task<HistoricalRangeResult> LoadRangeAsync(
        TimeRangeSelection range, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(range);

        var resolutionMs = ComputeResolutionMs(range);

        var timeline = await _daemonClient.GetAggregateTimelineAsync(
            BuildAggregateTimelineRequest(range, resolutionMs), cancellationToken);

        if (timeline.Points.Count == 0) {
            return new HistoricalRangeResult([], [], resolutionMs);
        }

        var summaries = await _daemonClient.GetProcessSummariesAsync(
            BuildProcessSummariesRequest(range), cancellationToken);

        return new HistoricalRangeResult(timeline.Points, summaries.Summaries, resolutionMs);
    }

    /// <summary>
    /// Loads the chart for a specific range + process selection. When
    /// <paramref name="processPath"/> is <c>null</c>, returns the aggregate-
    /// across-processes timeline (the "All processes" selection). Otherwise
    /// returns the per-process timeline for that path.
    /// </summary>
    public async Task<HistoricalChartResult> LoadProcessChartAsync(
        TimeRangeSelection range, string? processPath, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(range);

        var resolutionMs = ComputeResolutionMs(range);

        IReadOnlyList<TrafficTimePoint> points;
        if (processPath is null) {
            var response = await _daemonClient.GetAggregateTimelineAsync(
                BuildAggregateTimelineRequest(range, resolutionMs), cancellationToken);
            points = response.Points;
        } else {
            var response = await _daemonClient.GetProcessTimelineAsync(
                BuildProcessTimelineRequest(range, processPath, resolutionMs), cancellationToken);
            points = response.Points;
        }

        return new HistoricalChartResult(points, resolutionMs);
    }

    private static long ComputeResolutionMs(TimeRangeSelection range) {
        var spanMs = (long)(range.To - range.From).TotalMilliseconds;
        return Math.Max(spanMs / TargetOutputBuckets, MinResolutionMs);
    }

    private static GetAggregateTimelineRequest BuildAggregateTimelineRequest(
        TimeRangeSelection range, long resolutionMs) => new() {
            FromUnixNs = range.From.ToUnixTimeMilliseconds() * 1_000_000,
            ToUnixNs   = range.To.ToUnixTimeMilliseconds()   * 1_000_000,
            ResolutionMs = resolutionMs,
        };

    private static GetProcessSummariesRequest BuildProcessSummariesRequest(
        TimeRangeSelection range) => new() {
            FromUnixNs = range.From.ToUnixTimeMilliseconds() * 1_000_000,
            ToUnixNs   = range.To.ToUnixTimeMilliseconds()   * 1_000_000,
        };

    private static GetProcessTimelineRequest BuildProcessTimelineRequest(
        TimeRangeSelection range, string processPath, long resolutionMs) => new() {
            ProcessPath = processPath,
            FromUnixNs = range.From.ToUnixTimeMilliseconds() * 1_000_000,
            ToUnixNs   = range.To.ToUnixTimeMilliseconds()   * 1_000_000,
            ResolutionMs = resolutionMs,
        };
}

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

/// <summary>
/// Result of <see cref="HistoricalChartLoader.LoadProcessChartAsync"/>.
/// Per-process queries only build chart data — the sidebar stays on whatever
/// the last range load produced.
/// </summary>
internal sealed record HistoricalChartResult(
    IReadOnlyList<TrafficTimePoint> Points,
    long ResolutionMs);
