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

namespace Beholder.Ui.ViewModels;

internal sealed partial class TrafficTabViewModel : ViewModelBase {
    private readonly IDaemonClient _daemonClient;
    private readonly ProcessStateService _processStateService;
    private readonly Dictionary<string, ProcessListItem> _processLookup = new(StringComparer.Ordinal);
    private readonly ProcessListItem _allProcessesItem;
    private IReadOnlyDictionary<string, ProcessState>? _lastStates;

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

    public TrafficTabViewModel(IDaemonClient daemonClient, ProcessStateService processStateService) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(processStateService);
        _daemonClient = daemonClient;
        _processStateService = processStateService;

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
            // Switching back to live mode — rebuild chart from the current
            // circular buffer state immediately.
            ChartDataSpan = null;
            if (_lastStates is not null) {
                UpdateFromStates(_lastStates);
            }
        } else {
            // Switching to historical mode — query the daemon for the selected range.
            _ = LoadHistoricalRangeAsync(value);
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

        // Upsert process list items using recent-window sums for display + sort.
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

        SortProcessList();
        RebuildChartData(states);
    }

    private async Task LoadHistoricalRangeAsync(TimeRangeSelection range) {
        try {
            IsLoading = true;
            IsEmpty = false;

            var from = range.From;
            var to = range.To;
            var spanMs = (long)(to - from).TotalMilliseconds;
            var resolutionMs = Math.Max(spanMs / 300, 1000);

            // Query aggregate timeline for the chart
            var timelineRequest = new GetAggregateTimelineRequest {
                FromUnixNs = from.ToUnixTimeMilliseconds() * 1_000_000,
                ToUnixNs = to.ToUnixTimeMilliseconds() * 1_000_000,
                ResolutionMs = resolutionMs,
            };

            var timelineResponse = await _daemonClient.GetAggregateTimelineAsync(
                timelineRequest, CancellationToken.None);

            // Check that the user hasn't switched away while we were querying
            if (SelectedTimeRange != range) return;

            if (timelineResponse.Points.Count == 0) {
                IsEmpty = true;
                IsLoading = false;
                ChartData = [];
                return;
            }

            // Build chart data from the historical query
            var downloadColor = ThemeColorHelper.Resolve("ChartOutboundStrokeColor");
            var uploadColor = ThemeColorHelper.Resolve("ChartInboundStrokeColor");

            var downloadValues = new long[timelineResponse.Points.Count];
            var uploadValues = new long[timelineResponse.Points.Count];
            for (var i = 0; i < timelineResponse.Points.Count; i++) {
                downloadValues[i] = timelineResponse.Points[i].BytesIn;
                uploadValues[i] = timelineResponse.Points[i].BytesOut;
            }

            ChartDataSpan = to - from;
            ChartData = [
                new ChartSeries("Download", downloadValues, downloadColor),
                new ChartSeries("Upload", uploadValues, uploadColor),
            ];

            // Query per-process totals for the process list
            var snapshot = await _daemonClient.GetSnapshotAsync(CancellationToken.None);
            if (SelectedTimeRange != range) return;

            // Clear and rebuild the process list with historical totals
            _processLookup.Clear();
            while (ProcessList.Count > 1) ProcessList.RemoveAt(ProcessList.Count - 1);

            long allHistIn = 0;
            long allHistOut = 0;

            foreach (var snap in snapshot.Snapshots) {
                var procRequest = new GetProcessTimelineRequest {
                    ProcessPath = snap.ProcessPath,
                    FromUnixNs = from.ToUnixTimeMilliseconds() * 1_000_000,
                    ToUnixNs = to.ToUnixTimeMilliseconds() * 1_000_000,
                    ResolutionMs = spanMs,
                };

                try {
                    var procResponse = await _daemonClient.GetProcessTimelineAsync(
                        procRequest, CancellationToken.None);
                    if (SelectedTimeRange != range) return;

                    long totalIn = 0;
                    long totalOut = 0;
                    foreach (var point in procResponse.Points) {
                        totalIn += point.BytesIn;
                        totalOut += point.BytesOut;
                    }

                    if (totalIn + totalOut == 0) continue;

                    allHistIn += totalIn;
                    allHistOut += totalOut;

                    var item = new ProcessListItem(snap.ProcessPath, snap.ProcessName);
                    item.UpdateTraffic(totalIn, totalOut);
                    _processLookup[snap.ProcessPath] = item;
                    ProcessList.Add(item);
                } catch {
                    // Per-process query is best-effort
                }
            }

            _allProcessesItem.UpdateTraffic(allHistIn, allHistOut);
            SortProcessList();
            IsLoading = false;
        } catch (Exception) {
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
        if (_lastStates is not null && SelectedTimeRange.IsLive)
            RebuildChartData(_lastStates);
    }

    private void RemoveProcess(string processPath) {
        if (_processLookup.Remove(processPath, out var item))
            ProcessList.Remove(item);
    }

    private void RebuildChartData(IReadOnlyDictionary<string, ProcessState> states) {
        var selected = SelectedProcess;
        var showAll = selected is null || selected.IsAll;

        var downloadColor = ThemeColorHelper.Resolve("ChartOutboundStrokeColor");
        var uploadColor = ThemeColorHelper.Resolve("ChartInboundStrokeColor");

        long[] downloadValues;
        long[] uploadValues;

        if (showAll) {
            (downloadValues, uploadValues) = AggregateAll(states);
        } else if (states.TryGetValue(selected!.ProcessPath, out var state)) {
            downloadValues = BufferToArray(state.RecentDeltaIn);
            uploadValues = BufferToArray(state.RecentDeltaOut);
        } else {
            ChartData = [];
            return;
        }

        ChartData = [
            new ChartSeries("Download", downloadValues, downloadColor),
            new ChartSeries("Upload", uploadValues, uploadColor),
        ];
    }

    private static long[] BufferToArray(CircularBuffer<long> buffer) {
        var result = new long[buffer.Count];
        for (var i = 0; i < buffer.Count; i++)
            result[i] = buffer[i];
        return result;
    }

    private static (long[] Download, long[] Upload) AggregateAll(
        IReadOnlyDictionary<string, ProcessState> states) {
        var max = 0;
        foreach (var s in states.Values) {
            if (s.RecentDeltaIn.Count > max) max = s.RecentDeltaIn.Count;
            if (s.RecentDeltaOut.Count > max) max = s.RecentDeltaOut.Count;
        }
        var download = new long[max];
        var upload = new long[max];
        foreach (var s in states.Values) {
            var inBuf = s.RecentDeltaIn;
            var outBuf = s.RecentDeltaOut;
            var inOffset = max - inBuf.Count;
            var outOffset = max - outBuf.Count;
            for (var i = 0; i < inBuf.Count; i++)
                download[inOffset + i] += inBuf[i];
            for (var i = 0; i < outBuf.Count; i++)
                upload[outOffset + i] += outBuf[i];
        }
        return (download, upload);
    }
}
