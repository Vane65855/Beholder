using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using Beholder.Protocol.Local;
using Beholder.Ui.Controls;
using Beholder.Ui.Helpers;
using Beholder.Ui.Models;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Grpc.Core;

namespace Beholder.Ui.ViewModels;

internal sealed partial class TrafficTabViewModel : ViewModelBase {
    private readonly ProcessStateService _processStateService;
    private readonly HistoricalChartLoader _historicalChartLoader;
    private readonly Dictionary<string, ProcessListItem> _processLookup = new(StringComparer.Ordinal);
    private readonly ProcessListItem _allProcessesItem;
    private IReadOnlyDictionary<string, ProcessState>? _lastStates;

    // Cached live-mode chart buffers. Reused across 1-Hz RebuildChartData calls
    // once the live circular buffers saturate (~300 samples = 5 min). Aliasing
    // is safe: all mutations happen on the UI thread inside RebuildChartData,
    // and Avalonia's Render also runs on the UI thread, so there's no observer
    // between Array.Clear and the ChartData reassignment that would see
    // partial buffer state. Null until the first live tick.
    private long[]? _cachedDownloadBuffer;
    private long[]? _cachedUploadBuffer;

    // Cancellation source for the currently in-flight historical query (either
    // LoadHistoricalRangeAsync or LoadHistoricalChartForProcessAsync). Cancelled
    // whenever the user triggers a new range/process change that supersedes the
    // in-flight work, so the daemon can stop the superseded stitched query
    // instead of running it to completion with its response discarded.
    private CancellationTokenSource? _historicalCts;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private ProcessListItem? _selectedProcess;

    [ObservableProperty]
    private IReadOnlyList<ChartSeries>? _chartData;

    /// <summary>
    /// Total wall-clock span of the current chart data. Null for live mode
    /// (TrafficChartControl defaults to 1-sample-per-second labeling).
    /// Set to the queried range's duration for historical views.
    /// </summary>
    [ObservableProperty]
    private TimeSpan? _chartDataSpan;

    [ObservableProperty]
    private TimeRangeSelection _selectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last5Minutes);

    public ObservableCollection<ProcessListItem> ProcessList { get; } = [];

    public TrafficTabViewModel(
        IDaemonClient daemonClient,
        ProcessStateService processStateService,
        HistoricalChartLoader historicalChartLoader) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(processStateService);
        ArgumentNullException.ThrowIfNull(historicalChartLoader);
        _processStateService = processStateService;
        _historicalChartLoader = historicalChartLoader;

        _allProcessesItem = new ProcessListItem(string.Empty, "All processes", isAll: true);
        ProcessList.Add(_allProcessesItem);
        SelectedProcess = _allProcessesItem;

        processStateService.ProcessStatesUpdated += OnProcessStatesUpdated;
        daemonClient.StateChanged += OnDaemonStateChanged;
    }

    private void OnDaemonStateChanged(DaemonStatusInfo status) {
        Dispatcher.UIThread.Post(() => {
            if (status.State == ConnectionState.Connected) {
                HasError = false;
                ErrorMessage = string.Empty;
            } else if (status.State is ConnectionState.Disconnected or ConnectionState.Reconnecting) {
                HasError = true;
                ErrorMessage = "Daemon disconnected \u2014 showing last known data.";
                IsLoading = false;
            }
        });
    }

    private void OnProcessStatesUpdated(IReadOnlyDictionary<string, ProcessState> states) {
        Dispatcher.UIThread.Post(() => UpdateFromStates(states));
    }

    partial void OnSelectedTimeRangeChanged(TimeRangeSelection value) {
        if (value.IsLive) {
            // Switching TO live mode — cancel any in-flight historical query
            // (the live path doesn't hit the daemon, so we don't need a fresh
            // CTS) and clear historical-only entries left over from the
            // previous range. UpdateFromStates only upserts from live states
            // and can't remove processes that were populated via
            // GetProcessSummaries but aren't in the current live snapshot
            // (e.g., engine-evicted processes that only exist in SQL history).
            CancelInFlightHistoricalQuery();
            ClearProcessList();
            ChartDataSpan = null;
            if (_lastStates is not null) {
                UpdateFromStates(_lastStates);
            }
        } else {
            // Switching to historical mode — cancel any prior query and issue
            // a new one under a fresh token.
            var ct = StartNewHistoricalQuery();
            _ = LoadHistoricalRangeAsync(value, ct);
        }
    }

    /// <summary>
    /// Cancels any in-flight historical query and returns a fresh
    /// <see cref="CancellationToken"/> for the new one. Called at the entry of
    /// each <c>OnSelectedXxxChanged</c> branch that will fire a new historical
    /// load.
    /// </summary>
    private CancellationToken StartNewHistoricalQuery() {
        _historicalCts?.Cancel();
        _historicalCts?.Dispose();
        _historicalCts = new CancellationTokenSource();
        return _historicalCts.Token;
    }

    /// <summary>
    /// Cancels any in-flight historical query without starting a new one.
    /// Used by the live branch of <see cref="OnSelectedTimeRangeChanged"/>.
    /// </summary>
    private void CancelInFlightHistoricalQuery() {
        _historicalCts?.Cancel();
        _historicalCts?.Dispose();
        _historicalCts = null;
    }

    /// <summary>
    /// Removes all process-list items except the leading "All processes"
    /// aggregate row, and clears the lookup dictionary. Called on every range
    /// transition so one range's process set can't leak into another's sidebar.
    /// </summary>
    private void ClearProcessList() {
        _processLookup.Clear();
        while (ProcessList.Count > 1) ProcessList.RemoveAt(ProcessList.Count - 1);
    }

    internal void UpdateFromStates(IReadOnlyDictionary<string, ProcessState> states) {
        _lastStates = states;
        IsLoading = false;

        // In historical mode, live ticks still flow for the status strip, but
        // the chart and process list are frozen on the historical snapshot.
        if (!SelectedTimeRange.IsLive) return;

        if (states.Count == 0) {
            IsEmpty = true;
            return;
        }
        IsEmpty = false;

        UpsertProcessListFromStates(states);
        SortProcessList();
        RebuildChartData(states);
    }

    /// <summary>
    /// Upserts <see cref="ProcessList"/> items from live states, using each
    /// process's recent-window sum for display + sort; removes processes whose
    /// rolling window has gone to zero; updates the leading "All processes"
    /// aggregate row. Split out of <see cref="UpdateFromStates"/> to keep that
    /// method readable top-to-bottom as a narrative.
    /// </summary>
    private void UpsertProcessListFromStates(IReadOnlyDictionary<string, ProcessState> states) {
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
                RemoveProcess(kvp.Key);
                continue;
            }

            if (!_processLookup.TryGetValue(kvp.Key, out var item)) {
                item = new ProcessListItem(kvp.Key, state.DisplayName);
                _processLookup[kvp.Key] = item;
                ProcessList.Add(item);
            }
            item.UpdateTraffic(recentIn, recentOut);
        }

        _allProcessesItem.UpdateTraffic(allRecentIn, allRecentOut);
    }

    private async Task LoadHistoricalRangeAsync(TimeRangeSelection range, CancellationToken ct) {
        try {
            IsLoading = true;
            IsEmpty = false;

            var result = await _historicalChartLoader.LoadRangeAsync(range, ct);

            // The user may have switched away while we were querying.
            if (SelectedTimeRange != range) return;

            if (result.Points.Count == 0) {
                IsEmpty = true;
                IsLoading = false;
                ChartData = [];
                return;
            }

            ApplyHistoricalChart(result.Points, result.ResolutionMs);
            ApplyHistoricalProcessList(result.Summaries);
            IsLoading = false;
        } catch (OperationCanceledException) {
            // User switched range mid-query (or shutdown). No error banner —
            // the superseding query will take over. Re-throw so the Task
            // completes as Canceled rather than RanToCompletion.
            throw;
        } catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) {
            // gRPC surfaces CT cancellation as RpcException(Cancelled) in some
            // grpc-dotnet versions. Normalize to OCE so the fire-and-forget
            // task completes as Canceled rather than Faulted (which would fire
            // TaskScheduler.UnobservedTaskException at finalization).
            throw new OperationCanceledException("Cancelled via gRPC status", ex);
        } catch (RpcException) {
            // Historical query failed — show error state
            IsLoading = false;
            HasError = true;
            ErrorMessage = "Failed to load historical data.";
        }
    }

    private void SortProcessList() {
        // CRITICAL: never use indexer assignment (ProcessList[x] = y) on this
        // ObservableCollection — it fires NotifyCollectionChangedAction.Replace,
        // which causes Avalonia's ListBox to clear its SelectedItem when the
        // replaced index was the user's current selection. Use Move/Add/Remove
        // exclusively so identity is preserved and selection follows the item.
        if (ProcessList.Count <= 2) return;

        var sorted = new List<ProcessListItem>(ProcessList.Count - 1);
        for (var i = 1; i < ProcessList.Count; i++)
            sorted.Add(ProcessList[i]);
        sorted.Sort(static (a, b) => b.SortKey.CompareTo(a.SortKey));

        for (var targetIndex = 1; targetIndex <= sorted.Count; targetIndex++) {
            var desired = sorted[targetIndex - 1];
            var currentIndex = ProcessList.IndexOf(desired);
            if (currentIndex != targetIndex)
                ProcessList.Move(currentIndex, targetIndex);
        }
    }

    partial void OnSelectedProcessChanged(ProcessListItem? value) {
        if (value is null) {
            SelectedProcess = _allProcessesItem;
            return;
        }
        if (SelectedTimeRange.IsLive) {
            if (_lastStates is not null)
                RebuildChartData(_lastStates);
        } else {
            // In historical mode, re-query the chart for the selected process
            // (or the aggregate if "All processes" is selected). Cancel any
            // prior in-flight historical query first so rapid process-switching
            // doesn't leave superseded daemon work running.
            var ct = StartNewHistoricalQuery();
            _ = LoadHistoricalChartForProcessAsync(SelectedTimeRange, value, ct);
        }
    }

    private async Task LoadHistoricalChartForProcessAsync(
        TimeRangeSelection range, ProcessListItem selected, CancellationToken ct) {
        try {
            var processPath = selected.IsAll ? null : selected.ProcessPath;
            var result = await _historicalChartLoader.LoadProcessChartAsync(range, processPath, ct);

            if (SelectedTimeRange != range || SelectedProcess != selected) return;

            if (result.Points.Count == 0) {
                ChartData = [];
                return;
            }

            ApplyHistoricalChart(result.Points, result.ResolutionMs);
        } catch (OperationCanceledException) {
            throw;
        } catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) {
            // Same gRPC-cancellation bridge as LoadHistoricalRangeAsync.
            throw new OperationCanceledException("Cancelled via gRPC status", ex);
        } catch (RpcException) {
            // Per-process chart query failed — chart stays on previous state.
        }
    }

    private void RemoveProcess(string processPath) {
        if (_processLookup.Remove(processPath, out var item))
            ProcessList.Remove(item);
    }

    private void RebuildChartData(IReadOnlyDictionary<string, ProcessState> states) {
        var selected = SelectedProcess;
        var showAll = selected is null || selected.IsAll;

        long[] downloadValues;
        long[] uploadValues;

        if (showAll) {
            var length = ComputeMaxLength(states);
            downloadValues = RentOrAllocate(ref _cachedDownloadBuffer, length);
            uploadValues = RentOrAllocate(ref _cachedUploadBuffer, length);
            AggregateAllInto(states, downloadValues, uploadValues, length);
        } else if (states.TryGetValue(selected!.ProcessPath, out var state)) {
            downloadValues = RentOrAllocate(ref _cachedDownloadBuffer, state.RecentDeltaIn.Count);
            uploadValues = RentOrAllocate(ref _cachedUploadBuffer, state.RecentDeltaOut.Count);
            FillBufferInto(state.RecentDeltaIn, downloadValues);
            FillBufferInto(state.RecentDeltaOut, uploadValues);
        } else {
            ChartData = [];
            return;
        }

        var downloadColor = ThemeColorHelper.Resolve("ChartOutboundStrokeColor");
        var uploadColor = ThemeColorHelper.Resolve("ChartInboundStrokeColor");
        ChartData = [
            new ChartSeries("Download", downloadValues, downloadColor),
            new ChartSeries("Upload", uploadValues, uploadColor),
        ];
    }

    /// <summary>
    /// Transforms historical response points into download/upload arrays
    /// (with single-point padding applied), then writes <see cref="ChartData"/>
    /// and <see cref="ChartDataSpan"/>. Used by both the full-range and
    /// per-process historical loaders.
    /// </summary>
    private void ApplyHistoricalChart(IReadOnlyList<TrafficTimePoint> points, long resolutionMs) {
        var (downloadValues, uploadValues, dataSpanMs) =
            BuildChartArrays(points, resolutionMs);

        ChartDataSpan = TimeSpan.FromMilliseconds(Math.Max(dataSpanMs, 1000));

        var downloadColor = ThemeColorHelper.Resolve("ChartOutboundStrokeColor");
        var uploadColor = ThemeColorHelper.Resolve("ChartInboundStrokeColor");
        ChartData = [
            new ChartSeries("Download", downloadValues, downloadColor),
            new ChartSeries("Upload", uploadValues, uploadColor),
        ];
    }

    /// <summary>
    /// Pure transform: pulls <c>BytesIn</c>/<c>BytesOut</c> out of each point,
    /// computes the data-extent-based span from first/last timestamps, then
    /// delegates to <see cref="ApplySinglePointPadding"/> for the single-point
    /// spike layout. Callers must guarantee <c>points.Count &gt;= 1</c>.
    /// </summary>
    private static (long[] Download, long[] Upload, long DataSpanMs) BuildChartArrays(
        IReadOnlyList<TrafficTimePoint> points, long resolutionMs) {
        var downloadValues = new long[points.Count];
        var uploadValues = new long[points.Count];
        for (var i = 0; i < points.Count; i++) {
            downloadValues[i] = points[i].BytesIn;
            uploadValues[i] = points[i].BytesOut;
        }
        var dataFirstMs = points[0].TimestampUnixNs / 1_000_000;
        var dataLastMs = points[^1].TimestampUnixNs / 1_000_000;
        var dataSpanMs = dataLastMs - dataFirstMs;
        return ApplySinglePointPadding(downloadValues, uploadValues, dataSpanMs, resolutionMs);
    }

    /// <summary>
    /// Clears and rebuilds <see cref="ProcessList"/> from historical summaries,
    /// updates the leading "All processes" aggregate row, and re-sorts. Used
    /// only by <see cref="LoadHistoricalRangeAsync"/>; per-process chart
    /// queries leave the list untouched.
    /// </summary>
    private void ApplyHistoricalProcessList(IReadOnlyList<ProcessTrafficSummaryProto> summaries) {
        ClearProcessList();

        long allHistIn = 0;
        long allHistOut = 0;
        foreach (var summary in summaries) {
            allHistIn += summary.TotalBytesIn;
            allHistOut += summary.TotalBytesOut;
            var item = new ProcessListItem(summary.ProcessPath, summary.ProcessName);
            item.UpdateTraffic(summary.TotalBytesIn, summary.TotalBytesOut);
            _processLookup[summary.ProcessPath] = item;
            ProcessList.Add(item);
        }

        _allProcessesItem.UpdateTraffic(allHistIn, allHistOut);
        SortProcessList();
    }

    /// <summary>
    /// Pads a single-value download/upload array pair to an 11-point spike
    /// layout (one leading zero, the burst at index 1, nine trailing zeros)
    /// so <see cref="TrafficChartControl"/> can render a sharp up-and-down
    /// peak on the left with empty trailing space. Arrays with a count other
    /// than 1 are returned unchanged. When padding applies, the caller's
    /// <paramref name="dataSpanMs"/> is widened to 10× bucket width so the
    /// X-axis has ten equal divisions covering the spike plus trailing space.
    /// </summary>
    /// <remarks>
    /// Without this, <see cref="TrafficChartControl"/> early-returns for
    /// maxSamples &lt; 2 (NaN-guard), and axis labels hide below tickCount = 2.
    /// The spike layout was chosen in Phase 5.4.1 over a centered single dot
    /// so the peak reads as "this happened at the start of the window" rather
    /// than "this is a scatter plot."
    /// </remarks>
    private static (long[] Download, long[] Upload, long DataSpanMs) ApplySinglePointPadding(
        long[] downloadValues, long[] uploadValues, long dataSpanMs, long resolutionMs
    ) {
        if (downloadValues.Length != 1) return (downloadValues, uploadValues, dataSpanMs);
        return (
            [0L, downloadValues[0], 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L],
            [0L, uploadValues[0],   0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L],
            Math.Max(resolutionMs * 10, 10_000));
    }

    /// <summary>
    /// Rents <paramref name="cached"/> when its length exactly matches
    /// <paramref name="length"/> (cleared to zero for the caller); otherwise
    /// allocates a fresh <see langword="long"/>[]. In both cases
    /// <paramref name="cached"/> is updated to hold the returned buffer so
    /// the next tick sees it. Exact-size match (not grow-on-demand) because
    /// the caller passes the buffer straight through <see cref="ChartSeries.Values"/>
    /// where <c>.Count</c> is read by <see cref="TrafficChartControl"/> for
    /// X-axis scaling — an oversized buffer would throw off the chart.
    /// </summary>
    private static long[] RentOrAllocate(ref long[]? cached, int length) {
        long[] buffer;
        if (cached is not null && cached.Length == length) {
            buffer = cached;
            Array.Clear(buffer, 0, length);
        } else {
            buffer = new long[length];
        }
        cached = buffer;
        return buffer;
    }

    private static void FillBufferInto(CircularBuffer<long> source, long[] destination) {
        for (var i = 0; i < source.Count; i++)
            destination[i] = source[i];
    }

    private static int ComputeMaxLength(IReadOnlyDictionary<string, ProcessState> states) {
        var max = 0;
        foreach (var s in states.Values) {
            if (s.RecentDeltaIn.Count > max) max = s.RecentDeltaIn.Count;
            if (s.RecentDeltaOut.Count > max) max = s.RecentDeltaOut.Count;
        }
        return max;
    }

    /// <summary>
    /// Aggregates all processes' recent-window samples into the caller-owned
    /// <paramref name="download"/> and <paramref name="upload"/> buffers.
    /// Buffers must be pre-cleared and of length <paramref name="length"/>;
    /// right-aligns each process's samples so processes with shorter histories
    /// contribute zeros at the front of the window rather than being stretched.
    /// </summary>
    private static void AggregateAllInto(
        IReadOnlyDictionary<string, ProcessState> states,
        long[] download, long[] upload, int length) {
        foreach (var s in states.Values) {
            var inBuf = s.RecentDeltaIn;
            var outBuf = s.RecentDeltaOut;
            var inOffset = length - inBuf.Count;
            var outOffset = length - outBuf.Count;
            for (var i = 0; i < inBuf.Count; i++)
                download[inOffset + i] += inBuf[i];
            for (var i = 0; i < outBuf.Count; i++)
                upload[outOffset + i] += outBuf[i];
        }
    }
}
