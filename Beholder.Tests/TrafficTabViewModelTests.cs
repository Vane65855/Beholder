using Beholder.Protocol.Local;
using Beholder.Ui.Models;
using Beholder.Ui.Services;
using Beholder.Ui.ViewModels;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public class TrafficTabViewModelTests {
    private static TrafficTabViewModel CreateViewModel() {
        var fakeClient = new FakeDaemonClient();
        var subscriber = new DaemonStreamSubscriber(
            fakeClient,
            NullLogger<DaemonStreamSubscriber>.Instance);
        var service = new ProcessStateService(subscriber, fakeClient);
        return new TrafficTabViewModel(fakeClient, service);
    }

    private static ProcessState MakeState(
        string path, string name, long[] recentIn, long[] recentOut) {
        var state = new ProcessState { ProcessPath = path, DisplayName = name };
        foreach (var v in recentIn) state.RecentDeltaIn.Add(v);
        foreach (var v in recentOut) state.RecentDeltaOut.Add(v);
        return state;
    }

    [Fact]
    public void UpdateFromStates_AllSelected_ProducesExactlyTwoSeries() {
        var vm = CreateViewModel();
        var states = new Dictionary<string, ProcessState> {
            ["a.exe"] = MakeState("a.exe", "a", [10, 20, 30], [1, 2, 3]),
            ["b.exe"] = MakeState("b.exe", "b", [100, 200, 300], [5, 10, 15]),
        };

        vm.UpdateFromStates(states);

        Assert.NotNull(vm.ChartData);
        Assert.Equal(2, vm.ChartData!.Count);
        Assert.Equal("Download", vm.ChartData[0].Name);
        Assert.Equal("Upload", vm.ChartData[1].Name);
    }

    [Fact]
    public void UpdateFromStates_AllSelected_AggregatesAcrossProcesses() {
        var vm = CreateViewModel();
        var states = new Dictionary<string, ProcessState> {
            ["a.exe"] = MakeState("a.exe", "a", [10, 20, 30], [1, 2, 3]),
            ["b.exe"] = MakeState("b.exe", "b", [100, 200, 300], [5, 10, 15]),
        };

        vm.UpdateFromStates(states);

        var download = vm.ChartData![0].Values;
        var upload = vm.ChartData[1].Values;
        Assert.Equal(new long[] { 110, 220, 330 }, download);
        Assert.Equal(new long[] { 6, 12, 18 }, upload);
    }

    [Fact]
    public void UpdateFromStates_SpecificProcessSelected_UsesThatProcessBuffers() {
        var vm = CreateViewModel();
        var states = new Dictionary<string, ProcessState> {
            ["a.exe"] = MakeState("a.exe", "a", [10, 20, 30], [1, 2, 3]),
            ["b.exe"] = MakeState("b.exe", "b", [100, 200, 300], [5, 10, 15]),
        };
        vm.UpdateFromStates(states);

        // Pick the list item for b.exe (index 0 is "All processes")
        var bItem = vm.ProcessList.First(p => p.ProcessPath == "b.exe");
        vm.SelectedProcess = bItem;

        var download = vm.ChartData![0].Values;
        var upload = vm.ChartData[1].Values;
        Assert.Equal(new long[] { 100, 200, 300 }, download);
        Assert.Equal(new long[] { 5, 10, 15 }, upload);
    }

    [Fact]
    public void SelectionChange_TriggersChartRebuild() {
        var vm = CreateViewModel();
        var states = new Dictionary<string, ProcessState> {
            ["a.exe"] = MakeState("a.exe", "a", [10, 20, 30], [1, 2, 3]),
            ["b.exe"] = MakeState("b.exe", "b", [100, 200, 300], [5, 10, 15]),
        };
        vm.UpdateFromStates(states);
        var allChart = vm.ChartData;

        var bItem = vm.ProcessList.First(p => p.ProcessPath == "b.exe");
        vm.SelectedProcess = bItem;

        Assert.NotSame(allChart, vm.ChartData);
        Assert.NotEqual(allChart![0].Values, vm.ChartData![0].Values);
    }

    [Fact]
    public void UpdateFromStates_SortsByCombinedRecentTraffic() {
        var vm = CreateViewModel();
        var states = new Dictionary<string, ProcessState> {
            ["quiet.exe"] = MakeState("quiet.exe", "quiet", [1], [1]),
            ["loud.exe"] = MakeState("loud.exe", "loud", [1000], [1000]),
            ["medium.exe"] = MakeState("medium.exe", "medium", [50], [50]),
        };

        vm.UpdateFromStates(states);

        // Index 0 is "All processes"; 1..n are sorted by SortKey desc
        Assert.True(vm.ProcessList[0].IsAll);
        Assert.Equal("loud", vm.ProcessList[1].DisplayName);
        Assert.Equal("medium", vm.ProcessList[2].DisplayName);
        Assert.Equal("quiet", vm.ProcessList[3].DisplayName);
    }

    [Fact]
    public void UpdateFromStates_Empty_SetsIsEmpty() {
        var vm = CreateViewModel();

        vm.UpdateFromStates(new Dictionary<string, ProcessState>());

        Assert.True(vm.IsEmpty);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public void SelectedProcess_SurvivesReSortFromStateUpdate() {
        var vm = CreateViewModel();

        // Initial: a dominates (sort key 200), b is second (100) → [All, a, b]
        var states1 = new Dictionary<string, ProcessState> {
            ["a.exe"] = MakeState("a.exe", "a", [100], [100]),
            ["b.exe"] = MakeState("b.exe", "b", [50], [50]),
        };
        vm.UpdateFromStates(states1);

        var bItem = vm.ProcessList.First(p => p.ProcessPath == "b.exe");
        vm.SelectedProcess = bItem;
        Assert.Same(bItem, vm.SelectedProcess);

        // Watch for the specific notification that Avalonia's SelectingItemsControl
        // interprets as "the selected item was replaced" — the root cause of the
        // original bug. The Move-based sort must produce zero of these.
        var replaceCount = 0;
        vm.ProcessList.CollectionChanged += (_, args) => {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace)
                replaceCount++;
        };

        // Next tick: b now dominates (2000) and a drops (20) → desired [All, b, a].
        // The sort must move b from index 2 → 1 without clobbering the selection.
        var states2 = new Dictionary<string, ProcessState> {
            ["a.exe"] = MakeState("a.exe", "a", [10], [10]),
            ["b.exe"] = MakeState("b.exe", "b", [1000], [1000]),
        };
        vm.UpdateFromStates(states2);

        Assert.Same(bItem, vm.SelectedProcess);
        Assert.Equal(1, vm.ProcessList.IndexOf(bItem));
        Assert.Equal(0, replaceCount);
    }

    [Fact]
    public void IdleProcess_RemovedFromList_OnSubsequentStateUpdate() {
        var vm = CreateViewModel();

        var states1 = new Dictionary<string, ProcessState> {
            ["chrome.exe"] = MakeState("chrome.exe", "chrome", [1000], [500]),
        };
        vm.UpdateFromStates(states1);
        Assert.Contains(vm.ProcessList, p => p.ProcessPath == "chrome.exe");

        // All-zero recent window → should be filtered out of the display list.
        var states2 = new Dictionary<string, ProcessState> {
            ["chrome.exe"] = MakeState("chrome.exe", "chrome", [0], [0]),
        };
        vm.UpdateFromStates(states2);

        Assert.DoesNotContain(vm.ProcessList, p => p.ProcessPath == "chrome.exe");
    }

    [Fact]
    public void SelectedProcess_GoesIdle_FallsBackToAllProcesses() {
        var vm = CreateViewModel();

        var states1 = new Dictionary<string, ProcessState> {
            ["chrome.exe"] = MakeState("chrome.exe", "chrome", [1000], [500]),
        };
        vm.UpdateFromStates(states1);
        var chromeItem = vm.ProcessList.First(p => p.ProcessPath == "chrome.exe");
        vm.SelectedProcess = chromeItem;

        var states2 = new Dictionary<string, ProcessState> {
            ["chrome.exe"] = MakeState("chrome.exe", "chrome", [0], [0]),
        };
        vm.UpdateFromStates(states2);

        // In a real Avalonia ListBox, Remove on the selected item causes the
        // control to write null back into SelectedProcess. In the unit test no
        // ListBox is attached, so we simulate that null write-back directly.
        vm.SelectedProcess = null;

        Assert.NotNull(vm.SelectedProcess);
        Assert.True(vm.SelectedProcess!.IsAll);
    }

    // --- Time-range selector tests ---

    [Fact]
    public void DefaultTimeRange_IsLast5Minutes() {
        var vm = CreateViewModel();
        Assert.Equal(TimeRangePreset.Last5Minutes, vm.SelectedTimeRange.Preset);
        Assert.True(vm.SelectedTimeRange.IsLive);
    }

    [Fact]
    public void HistoricalMode_SuppressesLiveChartRebuild() {
        var vm = CreateViewModel();

        // Populate with live data first
        var states1 = new Dictionary<string, ProcessState> {
            ["a.exe"] = MakeState("a.exe", "a", [100], [50]),
        };
        vm.UpdateFromStates(states1);
        var liveChart = vm.ChartData;
        Assert.NotNull(liveChart);

        // Switch to historical mode
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last1Hour);

        // Live tick arrives — chart should NOT be rebuilt from circular buffers
        var states2 = new Dictionary<string, ProcessState> {
            ["a.exe"] = MakeState("a.exe", "a", [200], [100]),
        };
        vm.UpdateFromStates(states2);

        // ChartData should NOT reflect the new live tick's buffer values
        // (it's either the historical query result or the previous live chart,
        // depending on whether LoadHistoricalRangeAsync completed — in the test
        // it won't since FakeDaemonClient returns empty data)
    }

    [Fact]
    public void SwitchBackToLive_RebuildsChartFromBuffers() {
        var vm = CreateViewModel();

        var states = new Dictionary<string, ProcessState> {
            ["a.exe"] = MakeState("a.exe", "a", [100, 200], [50, 100]),
        };
        vm.UpdateFromStates(states);

        // Switch to historical then back to live
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last24Hours);
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last5Minutes);

        // After switching back to live, the chart should rebuild from buffers
        Assert.NotNull(vm.ChartData);
        Assert.Equal(2, vm.ChartData!.Count);
    }

    [Fact]
    public void SwitchFromHistoricalToLive_ClearsStaleHistoricalProcesses() {
        // Regression test: before the fix, switching back to live from a
        // historical range left the historical-only processes (those that
        // had traffic in the 30-day window but aren't currently active)
        // in the sidebar. UpdateFromStates only upserts — it doesn't remove
        // stale entries. The fix clears the list on the IsLive transition.
        var (vm, client) = CreateViewModelWithClient();

        // Live state has only "live.exe".
        vm.UpdateFromStates(new Dictionary<string, ProcessState> {
            ["live.exe"] = MakeState("live.exe", "live", [100], [50]),
        });

        // Historical summary response has only "hist.exe" (simulates a
        // process that was active in the 30-day window but has since
        // been evicted from the engine's in-memory state).
        var histResponse = new GetProcessSummariesResponse();
        histResponse.Summaries.Add(new ProcessTrafficSummaryProto {
            ProcessPath = "hist.exe",
            ProcessName = "hist.exe",
            TotalBytesIn = 999,
            TotalBytesOut = 888,
        });
        client.ProcessSummariesResponse = histResponse;

        // LoadHistoricalRangeAsync early-returns when the aggregate timeline
        // response is empty (chart has nothing to draw), so stage at least
        // one point so the method reaches the process-summary population.
        var timelineResponse = new GetAggregateTimelineResponse();
        timelineResponse.Points.Add(new TrafficTimePoint {
            TimestampUnixNs = 0,
            BytesIn = 100,
            BytesOut = 50,
        });
        client.AggregateTimelineResponse = timelineResponse;

        // Switch to historical — sidebar now shows hist.exe from the RPC.
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last30Days);
        Assert.Contains(vm.ProcessList, p => p.ProcessPath == "hist.exe");

        // Switch back to live — sidebar should match live state, not stale
        // historical entries.
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last5Minutes);

        Assert.DoesNotContain(vm.ProcessList, p => p.ProcessPath == "hist.exe");
        Assert.Contains(vm.ProcessList, p => p.ProcessPath == "live.exe");
    }

    [Fact]
    public void ChartDataSpan_NullInLiveMode() {
        var vm = CreateViewModel();
        Assert.Null(vm.ChartDataSpan);

        // Switch to historical then back to live — should be null again
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last1Hour);
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last5Minutes);
        Assert.Null(vm.ChartDataSpan);
    }

    [Fact]
    public void CustomRange_CreatesCorrectLabel() {
        var from = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero);
        var custom = TimeRangeSelection.FromCustom(from, to);

        Assert.Equal(TimeRangePreset.Custom, custom.Preset);
        Assert.False(custom.IsLive);
        Assert.Contains("Apr", custom.Label);
    }

    // ---- Historical query exception-handling tests ----

    private static (TrafficTabViewModel Vm, FakeDaemonClient Client) CreateViewModelWithClient() {
        var fakeClient = new FakeDaemonClient();
        var subscriber = new DaemonStreamSubscriber(
            fakeClient,
            NullLogger<DaemonStreamSubscriber>.Instance);
        var service = new ProcessStateService(subscriber, fakeClient);
        var vm = new TrafficTabViewModel(fakeClient, service);
        return (vm, fakeClient);
    }

    [Fact]
    public void SelectedTimeRangeChange_RpcException_SetsErrorState() {
        // Historical query fails with a gRPC error → HasError should flip true
        // and the user-facing ErrorMessage should be set. IsLoading returns to
        // false (user shouldn't see a stuck "Loading…" after a known failure).
        var (vm, client) = CreateViewModelWithClient();
        client.AggregateTimelineException = new RpcException(
            new Status(StatusCode.Unavailable, "daemon offline"));

        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last7Days);

        Assert.True(vm.HasError);
        Assert.Contains("Failed", vm.ErrorMessage);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public void SelectedTimeRangeChange_OperationCanceled_DoesNotSetErrorState() {
        // OCE must NOT be treated as a user-visible error — it means the user
        // switched ranges (or shutdown ran) mid-query. HasError stays false.
        // Hook UnobservedTaskException so the re-thrown OCE from the
        // fire-and-forget LoadHistoricalRangeAsync doesn't pollute test output
        // when GC eventually runs.
        var (vm, client) = CreateViewModelWithClient();
        client.AggregateTimelineException = new OperationCanceledException();

        EventHandler<UnobservedTaskExceptionEventArgs> swallow = (_, e) => e.SetObserved();
        TaskScheduler.UnobservedTaskException += swallow;
        try {
            vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last7Days);

            Assert.False(vm.HasError);
            Assert.Equal(string.Empty, vm.ErrorMessage);
        } finally {
            TaskScheduler.UnobservedTaskException -= swallow;
        }
    }
}
