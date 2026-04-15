using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
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
                // Historical data seeding is handled by ProcessStateService.SeedAsync,
                // which runs before the live stream starts and fires ProcessStatesUpdated.
                // The chart and process list populate from that event automatically.
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
        _lastStates = states;
        IsLoading = false;

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
                // Process is idle across its whole recent window — drop it from
                // the display list. The aggregate totals above already included
                // its zero contribution, and RebuildChartData/AggregateAll still
                // iterates the `states` dictionary directly, so the chart's
                // "All processes" view is unaffected by this display filter.
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

    private void SortProcessList() {
        // CRITICAL: never use indexer assignment (ProcessList[x] = y) on this
        // ObservableCollection — it fires NotifyCollectionChangedAction.Replace,
        // which causes Avalonia's ListBox to clear its SelectedItem when the
        // replaced index was the user's current selection. Use Move/Add/Remove
        // exclusively so identity is preserved and selection follows the item.
        if (ProcessList.Count <= 2) return;

        // Index 0 (_allProcessesItem) is pinned. Sort indices 1.. by SortKey desc
        // into a plain List copy, then reorder ProcessList via Move calls.
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
            // Selection was cleared — typically because the selected process
            // went idle and was removed from ProcessList, so Avalonia's
            // SelectingItemsControl wrote null back. Fall back to the pinned
            // "All processes" entry; this reassignment re-enters the handler
            // with the ALL item and takes the rebuild branch below.
            SelectedProcess = _allProcessesItem;
            return;
        }
        if (_lastStates is not null)
            RebuildChartData(_lastStates);
    }

    private void RemoveProcess(string processPath) {
        if (_processLookup.Remove(processPath, out var item))
            ProcessList.Remove(item);
    }

    private void RebuildChartData(IReadOnlyDictionary<string, ProcessState> states) {
        var selected = SelectedProcess;
        var showAll = selected is null || selected.IsAll;

        // ChartOutboundStroke = teal (download). ChartInboundStroke = purple (upload).
        // Token names are legacy — see DarkTheme.axaml §Data Visualization.
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
        // Right-align: find max buffer length, treat missing older samples as 0.
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
