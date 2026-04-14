using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    private const int MaxVisibleSeries = 8;

    private readonly IDaemonClient _daemonClient;
    private readonly ProcessStateService _processStateService;
    private readonly Dictionary<string, ProcessListItem> _processLookup = new(StringComparer.Ordinal);
    private readonly ProcessListItem _allProcessesItem;
    private bool _historicalDataLoaded;

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
                if (!_historicalDataLoaded)
                    _ = LoadHistoricalDataAsync();
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

    internal void UpdateFromStates(IReadOnlyDictionary<string, ProcessState> states) {
        IsLoading = false;

        if (states.Count == 0) {
            IsEmpty = true;
            return;
        }
        IsEmpty = false;

        // Upsert process list items
        long allTotalOut = 0;
        foreach (var kvp in states) {
            allTotalOut += kvp.Value.TotalBytesOut;

            if (!_processLookup.TryGetValue(kvp.Key, out var item)) {
                item = new ProcessListItem(kvp.Key, kvp.Value.DisplayName);
                _processLookup[kvp.Key] = item;
                ProcessList.Add(item);
            }
            item.UpdateTraffic(kvp.Value.TotalBytesOut);
        }

        _allProcessesItem.UpdateTraffic(allTotalOut);

        SortProcessList();
        RebuildChartData(states);
    }

    private void SortProcessList() {
        // Simple insertion sort — process list is typically small (< 50 items)
        for (var i = 2; i < ProcessList.Count; i++) {
            var current = ProcessList[i];
            var j = i - 1;
            while (j >= 1 && ProcessList[j].TotalBytesOut < current.TotalBytesOut) {
                ProcessList[j + 1] = ProcessList[j];
                j--;
            }
            ProcessList[j + 1] = current;
        }
    }

    partial void OnSelectedProcessChanged(ProcessListItem? value) {
        // Rebuild chart with current data when selection changes
    }

    private void RebuildChartData(IReadOnlyDictionary<string, ProcessState> states) {
        var selected = SelectedProcess;
        var showAll = selected is null || selected.IsAll;

        if (showAll) {
            ChartData = BuildStackedSeries(states);
        } else {
            ChartData = BuildSingleSeries(selected!, states);
        }
    }

    private List<ChartSeries> BuildStackedSeries(IReadOnlyDictionary<string, ProcessState> states) {
        var result = new List<ChartSeries>();
        var ranked = states.Values
            .OrderByDescending(s => s.TotalBytesOut)
            .Take(MaxVisibleSeries)
            .ToList();

        foreach (var state in ranked) {
            var recentOut = state.RecentDeltaOut.ToList();
            var seriesIndex = SeriesColorHelper.GetSeriesIndex(state.ProcessPath);
            var color = ResolveSeriesColor(seriesIndex);
            result.Add(new ChartSeries(state.DisplayName, recentOut, color));
        }

        // "Other" series: sum remaining processes
        if (states.Count > MaxVisibleSeries) {
            var otherProcesses = states.Values
                .OrderByDescending(s => s.TotalBytesOut)
                .Skip(MaxVisibleSeries)
                .ToList();

            if (otherProcesses.Count > 0) {
                var sampleCount = otherProcesses.Max(s => s.RecentDeltaOut.Count);
                var summed = new long[sampleCount];
                foreach (var p in otherProcesses) {
                    for (var i = 0; i < p.RecentDeltaOut.Count; i++)
                        summed[i] += p.RecentDeltaOut[i];
                }
                var otherColor = ResolveSeriesColor(7);
                result.Add(new ChartSeries("Other", summed, otherColor));
            }
        }

        return result;
    }

    private static List<ChartSeries> BuildSingleSeries(
        ProcessListItem selected,
        IReadOnlyDictionary<string, ProcessState> states) {
        if (!states.TryGetValue(selected.ProcessPath, out var state))
            return [];

        var recentOut = state.RecentDeltaOut.ToList();
        var seriesIndex = SeriesColorHelper.GetSeriesIndex(state.ProcessPath);
        var color = ResolveSeriesColor(seriesIndex);
        return [new ChartSeries(state.DisplayName, recentOut, color)];
    }

    private async Task LoadHistoricalDataAsync() {
        try {
            IsLoading = true;
            var now = DateTimeOffset.UtcNow;
            var from = now.AddMinutes(-5);

            var request = new GetAggregateTimelineRequest {
                FromUnixNs = from.ToUnixTimeMilliseconds() * 1_000_000,
                ToUnixNs = now.ToUnixTimeMilliseconds() * 1_000_000,
                ResolutionMs = 10_000,
            };

            var response = await _daemonClient.GetAggregateTimelineAsync(request, CancellationToken.None);
            _historicalDataLoaded = true;

            if (response.Points.Count == 0) {
                IsEmpty = true;
                IsLoading = false;
                return;
            }

            var values = new long[response.Points.Count];
            for (var i = 0; i < response.Points.Count; i++)
                values[i] = response.Points[i].BytesOut;

            var strokeColor = ResolveThemeColor("ChartOutboundStrokeColor");
            ChartData = [new ChartSeries("All processes", values, strokeColor)];
            IsLoading = false;
        } catch (Exception) {
            // Historical data is best-effort — live streaming will fill in
            _historicalDataLoaded = true;
            IsLoading = false;
        }
    }

    private static Color ResolveSeriesColor(int seriesIndex) {
        var key = SeriesColorHelper.GetColorResourceKey(seriesIndex);
        return ResolveThemeColor(key);
    }

    private static Color ResolveThemeColor(string resourceKey) {
        var app = Avalonia.Application.Current;
        if (app is not null
            && Avalonia.Controls.ResourceNodeExtensions.TryFindResource(app, resourceKey, out var obj)
            && obj is Color color)
            return color;
        return Colors.White;
    }
}
