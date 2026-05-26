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
    /// <param name="remoteAddress">
    /// Phase 9.6: optional IP filter. When non-null/non-empty, BOTH the
    /// aggregate timeline AND the per-process summaries are restricted to
    /// traffic exchanged with this remote address (backs the Scanner →
    /// Traffic cross-link). The chart and per-process list stay visually
    /// coherent: a chart spike attributable to a process is always visible
    /// in the list below.
    /// </param>
    public async Task<HistoricalRangeResult> LoadRangeAsync(
        TimeRangeSelection range, CancellationToken cancellationToken,
        string? remoteAddress = null) {
        ArgumentNullException.ThrowIfNull(range);

        var resolutionMs = ComputeResolutionMs(range);

        var timeline = await _daemonClient.GetAggregateTimelineAsync(
            BuildAggregateTimelineRequest(range, resolutionMs, remoteAddress), cancellationToken);

        if (timeline.Points.Count == 0) {
            return new HistoricalRangeResult([], [], resolutionMs);
        }

        var summaries = await _daemonClient.GetProcessSummariesAsync(
            BuildProcessSummariesRequest(range, remoteAddress), cancellationToken);

        return new HistoricalRangeResult(timeline.Points, summaries.Summaries, resolutionMs);
    }

    /// <summary>
    /// Loads the chart for a specific range + process selection. When
    /// <paramref name="processPath"/> is <c>null</c>, returns the aggregate-
    /// across-processes timeline (the "All processes" selection). Otherwise
    /// returns the per-process timeline for that path. Phase 9.6 fix:
    /// <paramref name="remoteAddress"/> is the optional IP filter — when set,
    /// restricts the timeline to traffic exchanged with that IP regardless of
    /// process scope.
    /// </summary>
    public async Task<HistoricalChartResult> LoadProcessChartAsync(
        TimeRangeSelection range, string? processPath, CancellationToken cancellationToken,
        string? remoteAddress = null) {
        ArgumentNullException.ThrowIfNull(range);

        var resolutionMs = ComputeResolutionMs(range);

        IReadOnlyList<TrafficTimePoint> points;
        if (processPath is null) {
            var response = await _daemonClient.GetAggregateTimelineAsync(
                BuildAggregateTimelineRequest(range, resolutionMs, remoteAddress), cancellationToken);
            points = response.Points;
        } else {
            var response = await _daemonClient.GetProcessTimelineAsync(
                BuildProcessTimelineRequest(range, processPath, resolutionMs, remoteAddress), cancellationToken);
            points = response.Points;
        }

        return new HistoricalChartResult(points, resolutionMs);
    }

    private static long ComputeResolutionMs(TimeRangeSelection range) {
        var spanMs = (long)(range.To - range.From).TotalMilliseconds;
        return Math.Max(spanMs / TargetOutputBuckets, MinResolutionMs);
    }

    private static GetAggregateTimelineRequest BuildAggregateTimelineRequest(
        TimeRangeSelection range, long resolutionMs, string? remoteAddress = null) => new() {
            FromUnixNs = range.From.ToUnixTimeMilliseconds() * 1_000_000,
            ToUnixNs   = range.To.ToUnixTimeMilliseconds()   * 1_000_000,
            ResolutionMs = resolutionMs,
            RemoteAddress = remoteAddress ?? string.Empty,
        };

    private static GetProcessSummariesRequest BuildProcessSummariesRequest(
        TimeRangeSelection range, string? remoteAddress = null) => new() {
            FromUnixNs = range.From.ToUnixTimeMilliseconds() * 1_000_000,
            ToUnixNs   = range.To.ToUnixTimeMilliseconds()   * 1_000_000,
            // Empty string = no filter (RPC contract), matches "null" semantically.
            RemoteAddress = remoteAddress ?? string.Empty,
        };

    private static GetProcessTimelineRequest BuildProcessTimelineRequest(
        TimeRangeSelection range, string processPath, long resolutionMs,
        string? remoteAddress = null) => new() {
            ProcessPath = processPath,
            FromUnixNs = range.From.ToUnixTimeMilliseconds() * 1_000_000,
            ToUnixNs   = range.To.ToUnixTimeMilliseconds()   * 1_000_000,
            ResolutionMs = resolutionMs,
            RemoteAddress = remoteAddress ?? string.Empty,
        };
}
