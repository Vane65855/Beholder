using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Beholder.Protocol.Local;
// Aliased imports for the two Beholder.Core types the MAP sub-view needs.
// A wildcard `using Beholder.Core;` would clash with Protocol.Local's
// TrafficTimePoint and CountryTrafficSummary — these proto types share
// names with the Core records by design (the protocol mirrors Core's
// shape) but live in different namespaces.
using CountryCode = Beholder.Core.CountryCode;
using CoreCountryTrafficSummary = Beholder.Core.CountryTrafficSummary;
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

    /// <summary>
    /// Owned CTS for the Phase 8 MAP sub-view's single-flight country-
    /// breakdown query. A second concurrent invocation (rapid view-mode
    /// flip, time-range change, or process selection change while MAP is
    /// active) cancels the prior fetch. Disposed in <see cref="Dispose"/>.
    /// </summary>
    private CancellationTokenSource? _mapCts;

    /// <summary>
    /// Owned CTS for the Phase 8 polish per-country-top-N hover fetch.
    /// Cancelled when the user hovers a different country or leaves the
    /// map; mirrors the <see cref="_mapCts"/> single-flight pattern.
    /// </summary>
    private CancellationTokenSource? _topDestCts;

    /// <summary>
    /// In-memory cache for the per-country top-N destinations fetched on
    /// hover. Keyed by <see cref="TopDestCacheKey"/> so the same country
    /// + range + process tuple resolves instantly on re-hover. Cleared
    /// whenever the time range or process selection changes (the cached
    /// rows would be stale against the new query).
    /// </summary>
    private readonly Dictionary<TopDestCacheKey, IReadOnlyList<DestinationRow>> _topDestCache = new();

    /// <summary>
    /// Cache key for the Phase 8 polish per-country destinations fetch.
    /// Nested in the parent file per CODING_STANDARDS.md §File Naming.
    /// </summary>
    private sealed record TopDestCacheKey(string Iso2, TimeRangeSelection Range, string? ProcessPath);

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

    /// <summary>
    /// Per-country byte totals for the MAP sub-view, filtered to real
    /// (non-sentinel) country codes — "--" (Local) and "??" (Unknown)
    /// surface in <see cref="LocalAndUnknownCaption"/> instead of on the
    /// map (no geographic location to plot them at). Null until the first
    /// successful fetch.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<CoreCountryTrafficSummary>? _mapCountries;

    /// <summary>
    /// Normalization ceiling for the heatmap ramp — the highest single-
    /// country total across <see cref="MapCountries"/>. Drives
    /// <c>HeatmapPalette.BrushFor</c>'s 5-stop selection.
    /// </summary>
    [ObservableProperty]
    private long _maxMapBytes;

    /// <summary>
    /// Caption strip rendered below the map showing the LAN (private-range)
    /// and Unknown-country byte totals that are excluded from the heatmap
    /// because they have no geographic location to plot at.
    /// </summary>
    [ObservableProperty]
    private string _localAndUnknownCaption = string.Empty;

    /// <summary>
    /// Alpha-2 ISO code of the country the user is currently hovering on
    /// the world map, or null when hovering ocean / outside. Bound
    /// <c>OneWayToSource</c> from <c>WorldMapControl.HoveredCountry</c>;
    /// drives <see cref="OnHoveredCountryChanged"/> which fetches the
    /// per-country top-N destinations.
    /// </summary>
    [ObservableProperty]
    private string? _hoveredCountry;

    /// <summary>
    /// Top-N destinations for the currently hovered country, fetched
    /// lazily via <see cref="FetchTopDestinationsAsync"/>. Null when no
    /// fetch has completed for the current hover. Drives the world-map
    /// tooltip's Populated state per UI_QUALITY_STANDARDS §3.3.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<DestinationRow>? _hoveredCountryDestinations;

    /// <summary>
    /// True while a hover-driven destination fetch is in flight; drives
    /// the tooltip's Loading-distinct-from-Empty-distinct-from-Populated
    /// state per UI_QUALITY_STANDARDS §3.1.
    /// </summary>
    [ObservableProperty]
    private bool _hoveredCountryDestinationsLoading;

    /// <summary>
    /// True when the most recent hover-driven fetch returned zero rows.
    /// Drives the tooltip's Empty state per §3.2 ("no resolved destinations").
    /// </summary>
    [ObservableProperty]
    private bool _hoveredCountryDestinationsEmpty;

    /// <summary>
    /// True when the most recent hover-driven fetch failed (RPC error or
    /// unexpected exception). Drives the tooltip's Failed state — silent
    /// degrade per the design discussion in the plan: destinations are
    /// opportunistic data, so the country name + total bytes header is
    /// still useful and no ErrorBanner is raised.
    /// </summary>
    [ObservableProperty]
    private bool _hoveredCountryDestinationsFailed;

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
        _mapCts?.Cancel();
        _mapCts?.Dispose();
        _topDestCts?.Cancel();
        _topDestCts?.Dispose();
    }

    [RelayCommand]
    private void SetGraphView() => ViewMode = TrafficViewMode.Graph;

    [RelayCommand]
    private void SetColsView() => ViewMode = TrafficViewMode.Cols;

    [RelayCommand]
    private void SetMapView() => ViewMode = TrafficViewMode.Map;

    partial void OnViewModeChanged(TrafficViewMode value) {
        if (value == TrafficViewMode.Cols) {
            // Refreshing on every GRAPH→COLS transition keeps the data in
            // sync with whatever range/process selection happened while the
            // user was on the chart. Cheap on small ranges; acceptable cost
            // on large ones since the user deliberately requested a view
            // change.
            _ = RefreshColsAsync();
        } else if (value == TrafficViewMode.Map) {
            _ = RefreshMapAsync();
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
                ClearError();
            } else if (status.State is ConnectionState.Disconnected or ConnectionState.Reconnecting) {
                HasError = true;
                ErrorMessage = "Daemon disconnected \u2014 showing last known data.";
                IsLoading = false;
            }
        });
    }

    /// <summary>
    /// Clears the error banner. Bound to the close-X on the inline
    /// <see cref="Beholder.Ui.Controls.ErrorBanner"/>; also called by every
    /// historical-load entry and by the daemon-reconnect handler so transient
    /// errors don't stick after recovery. See UI_DESIGN.md \u00a75.10.
    /// </summary>
    [RelayCommand]
    private void DismissError() => ClearError();

    private void ClearError() {
        HasError = false;
        ErrorMessage = string.Empty;
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

        // MAP heatmap follows the range too; the GetCountryBreakdown RPC
        // takes the same (from, to) shape as the COLS queries.
        if (ViewMode == TrafficViewMode.Map) {
            _ = RefreshMapAsync();
        }

        // Phase 8 polish: range change invalidates the per-country
        // destinations cache (rows would be against the old window).
        _topDestCache.Clear();
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
        ClearError();   // see UI_DESIGN.md §5.10 auto-clear
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

        if (ViewMode == TrafficViewMode.Map) {
            _ = RefreshMapAsync();
        }

        // Phase 8 polish: process change invalidates the per-country
        // destinations cache (rows would be against the old process filter).
        _topDestCache.Clear();
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

    /// <summary>
    /// Phase 8 MAP sub-view: fetches the per-country byte breakdown from the
    /// daemon and splits it into (a) real countries that go on the heatmap
    /// and (b) the LAN / Unknown sentinels that surface in the caption
    /// below the map. Single-flight: a second concurrent invocation cancels
    /// the prior one via the owned CTS so rapid view-mode / range / process
    /// changes don't stack RPCs.
    /// </summary>
    private async Task RefreshMapAsync() {
        // Cancel any in-flight country breakdown; replace the CTS for the
        // new attempt. Mirrors ProcessListCoordinator's per-VM CTS pattern.
        _mapCts?.Cancel();
        _mapCts?.Dispose();
        _mapCts = new CancellationTokenSource();
        var ct = _mapCts.Token;

        var processPath = SelectedProcess is null || SelectedProcess.IsAll
            ? null
            : SelectedProcess.ProcessPath;
        var range = SelectedTimeRange.Resolve();

        ClearError();   // see UI_DESIGN.md §5.10 auto-clear
        try {
            var request = new GetCountryBreakdownRequest {
                FromUnixNs = range.From.ToUnixTimeMilliseconds() * 1_000_000L,
                ToUnixNs = range.To.ToUnixTimeMilliseconds() * 1_000_000L,
                ProcessPath = processPath ?? string.Empty,
            };
            var response = await _daemonClient.GetCountryBreakdownAsync(request, ct);
            if (ct.IsCancellationRequested) return;
            ApplyMapBreakdown(response);
        } catch (OperationCanceledException) {
            // Superseded — newer call owns the UI state. Don't re-throw;
            // returning lets the next call's await proceed normally.
        } catch (RpcException ex) {
            HasError = true;
            ErrorMessage = $"Failed to load country breakdown: {ex.Status.Detail}";
        }
    }

    private void ApplyMapBreakdown(GetCountryBreakdownResponse response) {
        var real = new List<CoreCountryTrafficSummary>(response.Countries.Count);
        long localIn = 0, localOut = 0, unknownIn = 0, unknownOut = 0;
        long maxBytes = 0;

        foreach (var c in response.Countries) {
            // CountryCode.Local = "--" (private/reserved IPs);
            // CountryCode.Unknown = "??" (resolved to no country).
            // Both have no geographic location so they go in the caption.
            switch (c.Country) {
                case "--":
                    localIn += c.TotalBytesIn;
                    localOut += c.TotalBytesOut;
                    break;
                case "??":
                    unknownIn += c.TotalBytesIn;
                    unknownOut += c.TotalBytesOut;
                    break;
                default:
                    real.Add(new CoreCountryTrafficSummary(
                        CountryCode.FromAlpha2(c.Country), c.TotalBytesIn, c.TotalBytesOut));
                    var total = c.TotalBytesIn + c.TotalBytesOut;
                    if (total > maxBytes) maxBytes = total;
                    break;
            }
        }

        MapCountries = real;
        MaxMapBytes = maxBytes;
        LocalAndUnknownCaption = FormatLocalAndUnknownCaption(localIn, localOut, unknownIn, unknownOut);
    }

    private static string FormatLocalAndUnknownCaption(
        long localIn, long localOut, long unknownIn, long unknownOut
    ) {
        // Show both pairs even at zero so the caption strip's height stays
        // stable across data updates (no layout jitter in the surrounding
        // DockPanel).
        return $"LAN: ▼ {FormatBytesShort(localIn)} / ▲ {FormatBytesShort(localOut)}"
            + $"   ·   Unknown: ▼ {FormatBytesShort(unknownIn)} / ▲ {FormatBytesShort(unknownOut)}";
    }

    private static string FormatBytesShort(long bytes) {
        const double KB = 1024, MB = KB * 1024, GB = MB * 1024;
        if (bytes >= GB) return $"{bytes / GB:F2} GB";
        if (bytes >= MB) return $"{bytes / MB:F1} MB";
        if (bytes >= KB) return $"{bytes / KB:F0} KB";
        return $"{bytes} B";
    }

    /// <summary>
    /// Phase 8 polish: drives the world-map hover tooltip's top-N
    /// destinations rows. Clears any prior state immediately so the
    /// previous country's tooltip can't bleed into the new one, then
    /// either hits the cache or fires a fresh fetch.
    /// </summary>
    partial void OnHoveredCountryChanged(string? value) {
        // Clear all four state flags + the rows themselves immediately so
        // the previous country's tooltip can't appear stale during the
        // brief moment between hover-change and fetch-land.
        HoveredCountryDestinations = null;
        HoveredCountryDestinationsLoading = false;
        HoveredCountryDestinationsEmpty = false;
        HoveredCountryDestinationsFailed = false;

        if (value is null) return;   // hover left the map

        var processPath = SelectedProcess is null || SelectedProcess.IsAll
            ? null
            : SelectedProcess.ProcessPath;
        var key = new TopDestCacheKey(value, SelectedTimeRange, processPath);
        if (_topDestCache.TryGetValue(key, out var cached)) {
            HoveredCountryDestinations = cached;
            HoveredCountryDestinationsEmpty = cached.Count == 0;
            return;
        }

        _topDestCts?.Cancel();
        _topDestCts?.Dispose();
        _topDestCts = new CancellationTokenSource();
        HoveredCountryDestinationsLoading = true;
        _ = FetchTopDestinationsAsync(value, key, _topDestCts.Token);
    }

    private async Task FetchTopDestinationsAsync(string iso2, TopDestCacheKey key, CancellationToken ct) {
        try {
            var range = SelectedTimeRange.Resolve();
            var request = new GetProcessDestinationsRequest {
                ProcessPath = key.ProcessPath ?? string.Empty,
                FromUnixNs = range.From.ToUnixTimeMilliseconds() * 1_000_000L,
                ToUnixNs = range.To.ToUnixTimeMilliseconds() * 1_000_000L,
                Country = iso2,
                Limit = WorldMapTooltipRenderer.Top3DestinationsLimit,
            };
            var response = await _daemonClient.GetProcessDestinationsAsync(request, ct);
            if (ct.IsCancellationRequested) return;

            var rows = new List<DestinationRow>(response.Destinations.Count);
            foreach (var d in response.Destinations) {
                // Hostname fallback to raw IP per the COLS view's pattern.
                var label = string.IsNullOrEmpty(d.Hostname) ? d.RemoteAddress : d.Hostname;
                rows.Add(new DestinationRow(label, d.TotalBytesIn + d.TotalBytesOut));
            }

            // Only apply if the user is still hovering this country; the
            // cache always gets written so a back-hover hits it instantly.
            _topDestCache[key] = rows;
            if (HoveredCountry == iso2) {
                HoveredCountryDestinationsLoading = false;
                HoveredCountryDestinations = rows;
                HoveredCountryDestinationsEmpty = rows.Count == 0;
            }
        } catch (OperationCanceledException) {
            // Superseded by a later hover; the new fetch owns the UI state.
        } catch (Exception) {
            // Silent-degrade per design: destinations are opportunistic
            // augmentation, the country name + bytes header is still useful,
            // and an ErrorBanner would obscure the map. Only flip the
            // Failed flag if the user is still hovering this country.
            if (HoveredCountry == iso2) {
                HoveredCountryDestinationsLoading = false;
                HoveredCountryDestinationsFailed = true;
            }
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
