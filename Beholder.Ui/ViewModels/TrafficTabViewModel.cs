using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Media;
using Beholder.Protocol.Local;
using Beholder.Ui.Controls;
using Beholder.Ui.Helpers;
using Beholder.Ui.Models;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;

namespace Beholder.Ui.ViewModels;

internal sealed partial class TrafficTabViewModel : ViewModelBase, IDisposable {
    private readonly IDaemonClient _daemonClient;
    private readonly ProcessStateService _processStateService;
    private readonly IDispatcher _dispatcher;
    private readonly ProcessListCoordinator _processList;
    private readonly HistoricalQueryOrchestrator _historicalQueries;
    private TrafficColsViewModel? _colsVm;
    private IReadOnlyDictionary<string, ProcessState>? _lastStates;

    // Cached live-mode chart buffers. Reused across 1-Hz RebuildChartData calls
    // once the live circular buffers saturate (~300 samples = 5 min). Aliasing
    // is safe: all mutations happen on the UI thread inside RebuildChartData,
    // and Avalonia's Render also runs on the UI thread, so there's no observer
    // between Array.Clear and the ChartData reassignment that would see
    // partial buffer state. Null until the first live tick.
    private long[]? _cachedDownloadBuffer;
    private long[]? _cachedUploadBuffer;

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

    /// <summary>
    /// Which sub-view the chart area is showing. Phase 6.3 adds COLS; MAP
    /// stays deferred until Phase 8. Defaults to GRAPH so the existing
    /// on-launch experience is unchanged.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGraphActive))]
    [NotifyPropertyChangedFor(nameof(IsColsActive))]
    [NotifyPropertyChangedFor(nameof(IsMapActive))]
    private TrafficViewMode _viewMode = TrafficViewMode.Graph;

    public bool IsGraphActive => ViewMode == TrafficViewMode.Graph;
    public bool IsColsActive => ViewMode == TrafficViewMode.Cols;
    public bool IsMapActive => ViewMode == TrafficViewMode.Map;

    public ObservableCollection<ProcessListItem> ProcessList => _processList.List;

    /// <summary>
    /// Lazy-created COLS view-model — instantiated on first COLS activation
    /// and cached for the tab's lifetime. Kept around so switching back to
    /// COLS without a range/process change doesn't re-fetch unnecessarily.
    /// </summary>
    public TrafficColsViewModel ColsVm => _colsVm ??= new TrafficColsViewModel(_daemonClient);

    public TrafficTabViewModel(
        IDaemonClient daemonClient,
        ProcessStateService processStateService,
        HistoricalChartLoader historicalChartLoader,
        IDispatcher dispatcher) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(processStateService);
        ArgumentNullException.ThrowIfNull(historicalChartLoader);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _daemonClient = daemonClient;
        _processStateService = processStateService;
        _dispatcher = dispatcher;
        _processList = new ProcessListCoordinator();
        _historicalQueries = new HistoricalQueryOrchestrator(historicalChartLoader);

        SelectedProcess = _processList.AllProcessesItem;

        _processStateService.ProcessStatesUpdated += OnProcessStatesUpdated;
        _daemonClient.StateChanged += OnDaemonStateChanged;
    }

    public void Dispose() {
        _processStateService.ProcessStatesUpdated -= OnProcessStatesUpdated;
        _daemonClient.StateChanged -= OnDaemonStateChanged;
        _historicalQueries.Dispose();
        _colsVm?.Dispose();
    }

    [RelayCommand]
    private void SetGraphView() => ViewMode = TrafficViewMode.Graph;

    [RelayCommand]
    private void SetColsView() => ViewMode = TrafficViewMode.Cols;

    partial void OnViewModeChanged(TrafficViewMode value) {
        if (value == TrafficViewMode.Cols) {
            // Refreshing on every GRAPH→COLS transition keeps the data in
            // sync with whatever range/process selection happened while the
            // user was on the chart. Cheap on small ranges; acceptable cost
            // on large ones since the user deliberately requested a view
            // change.
            _ = RefreshColsAsync();
        }
    }

    private Task RefreshColsAsync() {
        var processPath = SelectedProcess is null || SelectedProcess.IsAll
            ? null
            : SelectedProcess.ProcessPath;
        // Resolve the preset against the current wall clock — otherwise the
        // default "Last 5 Minutes" window stays pinned to app-start and every
        // COLS query looks at an empty pre-launch window.
        var range = SelectedTimeRange.Resolve();
        try {
            return ColsVm.RefreshAsync(range, processPath);
        } catch (OperationCanceledException) {
            // Superseded by a later refresh — nothing to do, the next call
            // owns the UI state.
            return Task.CompletedTask;
        }
    }

    private void OnDaemonStateChanged(DaemonStatusInfo status) {
        _dispatcher.Post(() => {
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
        _dispatcher.Post(() => {
            UpdateFromStates(states);
            // Live COLS refresh: same cadence as the chart (1 Hz, driven by
            // the daemon's snapshot broadcast). ColsVm.RefreshAsync cancels
            // any in-flight refresh via its owned CTS, so we never stack
            // RPCs even if a tick fires before the previous trio of
            // GetProcessDestinations / GetProtocolBreakdown /
            // GetCountryBreakdown calls completes. Skipped in historical
            // mode where the data is fixed for the queried range.
            if (ViewMode == TrafficViewMode.Cols && SelectedTimeRange.IsLive) {
                _ = RefreshColsAsync();
            }
        });
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
            _historicalQueries.CancelInFlight();
            _processList.Clear();
            ChartDataSpan = null;
            if (_lastStates is not null) {
                UpdateFromStates(_lastStates);
            }
        } else {
            // Switching to historical mode — orchestrator cancels any prior
            // query and issues a new one under a fresh token.
            _ = LoadHistoricalRangeAsync(value);
        }

        // COLS columns follow the range regardless of live/historical
        // distinction (the 3 RPCs accept any window — the daemon chooses the
        // tier internally).
        if (ViewMode == TrafficViewMode.Cols) {
            _ = RefreshColsAsync();
        }
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

        _processList.Upsert(states);
        _processList.Sort();
        RebuildChartData(states);
    }

    private async Task LoadHistoricalRangeAsync(TimeRangeSelection range) {
        try {
            IsLoading = true;
            IsEmpty = false;

            var result = await _historicalQueries.LoadRangeAsync(range);

            // The user may have switched away while we were querying.
            if (SelectedTimeRange != range) return;

            if (result.Points.Count == 0) {
                IsEmpty = true;
                IsLoading = false;
                ChartData = [];
                return;
            }

            ApplyHistoricalChart(result.Points, result.ResolutionMs);
            _processList.ApplyHistorical(result.Summaries);
            IsLoading = false;
        } catch (OperationCanceledException) {
            // User switched range mid-query (or shutdown). No error banner —
            // the superseding query will take over. Re-throw so the Task
            // completes as Canceled rather than RanToCompletion.
            throw;
        } catch (RpcException) {
            // Historical query failed — show error state
            IsLoading = false;
            HasError = true;
            ErrorMessage = "Failed to load historical data.";
        }
    }

    partial void OnSelectedProcessChanged(ProcessListItem? value) {
        if (value is null) {
            SelectedProcess = _processList.AllProcessesItem;
            return;
        }
        if (SelectedTimeRange.IsLive) {
            if (_lastStates is not null)
                RebuildChartData(_lastStates);
        } else {
            // In historical mode, re-query the chart for the selected process
            // (or the aggregate if "All processes" is selected). The
            // orchestrator cancels any prior in-flight historical query so
            // rapid process-switching doesn't leave superseded daemon work
            // running. Resolve the preset so the window tracks the current
            // wall clock instead of whenever the dropdown was last touched.
            _ = LoadHistoricalChartForProcessAsync(SelectedTimeRange.Resolve(), value);
        }

        if (ViewMode == TrafficViewMode.Cols) {
            _ = RefreshColsAsync();
        }
    }

    private async Task LoadHistoricalChartForProcessAsync(
        TimeRangeSelection range, ProcessListItem selected) {
        try {
            var processPath = selected.IsAll ? null : selected.ProcessPath;
            var result = await _historicalQueries.LoadProcessChartAsync(range, processPath);

            if (!SelectedTimeRange.IsSameSelectionAs(range) || SelectedProcess != selected) return;

            if (result.Points.Count == 0) {
                ChartData = [];
                return;
            }

            ApplyHistoricalChart(result.Points, result.ResolutionMs);
        } catch (OperationCanceledException) {
            throw;
        } catch (RpcException) {
            // Per-process chart query failed — chart stays on previous state.
        }
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
