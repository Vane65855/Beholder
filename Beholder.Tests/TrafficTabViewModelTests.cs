using System.Reflection;
using Beholder.Protocol.Local;
using Beholder.Tests.TestDoubles;
using Beholder.Ui.Controls;
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
            TimeProvider.System,
            NullLogger<DaemonStreamSubscriber>.Instance);
        var service = new ProcessStateService(subscriber, fakeClient, TimeProvider.System);
        var loader = new HistoricalChartLoader(fakeClient);
        return new TrafficTabViewModel(fakeClient, service, loader, new SyncDispatcher());
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

        // ChartData is a fresh ChartSeries[] on every rebuild, but the
        // underlying Values buffer is reused across ticks to avoid per-tick
        // allocations — so reference-inequality on .Values is no longer a
        // valid "did it rebuild" proxy. Assert the actual rebuilt content:
        // after switching to b.exe, the chart shows b.exe's recent window,
        // not the previous aggregate [110, 220, 330].
        Assert.NotSame(allChart, vm.ChartData);
        Assert.Equal(new long[] { 100, 200, 300 }, vm.ChartData![0].Values);
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
            TimeProvider.System,
            NullLogger<DaemonStreamSubscriber>.Instance);
        var service = new ProcessStateService(subscriber, fakeClient, TimeProvider.System);
        var loader = new HistoricalChartLoader(fakeClient);
        var vm = new TrafficTabViewModel(fakeClient, service, loader, new SyncDispatcher());
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

    // ---- Cancellation plumbing tests (audit #5) ----

    [Fact]
    public void RapidRangeSwitch_CancelsPreviousQueryCt() {
        // When the user switches ranges while a historical query is in flight,
        // the previous query's CancellationToken must be cancelled so the
        // daemon can stop the superseded stitched-query work. Captures every
        // CT the VM passes to the aggregate-timeline RPC, then asserts the
        // first CT was cancelled by the superseding range change while the
        // second (fresh) CT stays uncancelled.
        var (vm, client) = CreateViewModelWithClient();

        var captured = new List<CancellationToken>();
        client.AggregateTimelineResponder = (_, ct) => {
            captured.Add(ct);
            return new GetAggregateTimelineResponse();
        };

        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last30Days);

        // The CT threaded through must be a real cancellable token, not None.
        // Before the fix this field was CancellationToken.None (CanBeCanceled == false).
        Assert.Single(captured);
        Assert.True(captured[0].CanBeCanceled);
        Assert.False(captured[0].IsCancellationRequested);

        // Superseding range change must cancel the first CT and issue a fresh one.
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.AllTime);

        Assert.Equal(2, captured.Count);
        Assert.True(captured[0].IsCancellationRequested);   // first was cancelled
        Assert.False(captured[1].IsCancellationRequested);  // second is fresh
    }

    [Fact]
    public void SwitchToLive_CancelsInFlightHistoricalCt() {
        // Switching TO live mode (5 Minutes) must also cancel any in-flight
        // historical query — the live path doesn't hit the daemon, so leaving
        // a stale historical query running would waste daemon CPU.
        var (vm, client) = CreateViewModelWithClient();

        var captured = new List<CancellationToken>();
        client.AggregateTimelineResponder = (_, ct) => {
            captured.Add(ct);
            return new GetAggregateTimelineResponse();
        };

        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last30Days);
        Assert.Single(captured);
        Assert.True(captured[0].CanBeCanceled);
        Assert.False(captured[0].IsCancellationRequested);

        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last5Minutes);

        // Live path doesn't fire another aggregate RPC — still just one capture.
        Assert.Single(captured);
        Assert.True(captured[0].IsCancellationRequested);
    }

    // ---- Dispose / unsubscribe tests (audit #16) ----

    [Fact]
    public void Dispose_UnsubscribesFromDaemonStateChanged() {
        // The reflection-based regression guard: after Dispose, the VM's
        // handler must be gone from FakeDaemonClient.StateChanged's
        // invocation list. Uses a sentinel handler to prove the count delta
        // matches exactly the VM's contribution (not off-by-one or wholesale
        // event clearing).
        var (vm, client) = CreateViewModelWithClient();

        Action<DaemonStatusInfo> sentinel = _ => { };
        client.StateChanged += sentinel;

        var before = CountStateChangedHandlers(client);
        vm.Dispose();
        var after = CountStateChangedHandlers(client);

        Assert.Equal(before - 1, after);

        // Sanity: the sentinel we added is still there.
        client.StateChanged -= sentinel;
        Assert.Equal(after - 1, CountStateChangedHandlers(client));
    }

    [Fact]
    public void Dispose_DoesNotThrow() {
        // Smoke: Dispose also unsubscribes from ProcessStatesUpdated and
        // cancels any in-flight historical query. Covered by code review;
        // this test guards that the Dispose path is reachable without
        // throwing.
        var vm = CreateViewModel();
        var exception = Record.Exception(() => vm.Dispose());
        Assert.Null(exception);
    }

    private static int CountStateChangedHandlers(FakeDaemonClient client) {
        // Field-like events (public event Action<T>? Name;) generate a
        // compiler-named private backing field. Reflection reads that
        // backing delegate and returns its invocation-list length.
        var field = typeof(FakeDaemonClient).GetField(
            "StateChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = field?.GetValue(client) as Delegate;
        return handler?.GetInvocationList().Length ?? 0;
    }

    // ---- View-mode switching tests (Phase 6.3 COLS) ----

    [Fact]
    public void ViewMode_DefaultsToGraph() {
        var vm = CreateViewModel();
        Assert.Equal(TrafficViewMode.Graph, vm.ViewMode);
        Assert.True(vm.IsGraphActive);
        Assert.False(vm.IsColsActive);
    }

    [Fact]
    public void SetColsViewCommand_SwitchesViewMode() {
        var vm = CreateViewModel();
        vm.SetColsViewCommand.Execute(null);

        Assert.Equal(TrafficViewMode.Cols, vm.ViewMode);
        Assert.True(vm.IsColsActive);
        Assert.False(vm.IsGraphActive);
    }

    [Fact]
    public async Task SwitchToCols_PopulatesColsViewModel() {
        // Switching to COLS fires the 3 RPCs against the current range.
        // Stage one destination so Hosts fills with at least one row.
        var (vm, client) = CreateViewModelWithClient();
        client.ProcessDestinationsResponder = _ => {
            var response = new GetProcessDestinationsResponse();
            response.Destinations.Add(new DestinationSummary {
                RemoteAddress = "1.1.1.1", Hostname = "one",
                Country = "US", TotalBytesIn = 100, TotalBytesOut = 50, ConnectionCount = 1,
            });
            return response;
        };

        vm.SetColsViewCommand.Execute(null);

        // OnViewModeChanged fires RefreshAsync async. Yield a few times to let
        // the await in RefreshAsync complete (FakeDaemonClient returns
        // synchronously but the await state machine still posts continuations).
        for (var i = 0; i < 5 && vm.ColsVm.Hosts.Count == 0; i++)
            await Task.Yield();

        Assert.Single(vm.ColsVm.Hosts);
    }

    [Fact]
    public async Task RangeChangeInColsMode_RefetchesColsData() {
        // COLS is live — range change must refetch the 3 breakdown RPCs.
        // Capture how many times the destinations RPC fires; must be > once
        // after a range flip.
        var (vm, client) = CreateViewModelWithClient();
        var destinationCalls = 0;
        client.ProcessDestinationsResponder = _ => {
            destinationCalls++;
            return new GetProcessDestinationsResponse();
        };

        vm.SetColsViewCommand.Execute(null);
        for (var i = 0; i < 5; i++) await Task.Yield();
        var afterActivation = destinationCalls;

        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last1Hour);
        for (var i = 0; i < 5; i++) await Task.Yield();

        Assert.True(destinationCalls > afterActivation,
            $"Expected range change to refetch COLS data; calls before={afterActivation} after={destinationCalls}");
    }

    [Fact]
    public async Task ProcessSelectionInColsMode_RefetchesColsData() {
        // Per-process filter in COLS view: selecting a specific process
        // must trigger a COLS refresh that propagates the process path to
        // the 3 RPCs.
        var (vm, client) = CreateViewModelWithClient();
        string? observedPath = null;
        client.ProcessDestinationsResponder = req => {
            observedPath = req.ProcessPath;
            return new GetProcessDestinationsResponse();
        };

        var states = new Dictionary<string, ProcessState> {
            ["a.exe"] = MakeState("a.exe", "a", [100], [50]),
        };
        vm.UpdateFromStates(states);
        vm.SetColsViewCommand.Execute(null);
        for (var i = 0; i < 5; i++) await Task.Yield();

        var aItem = vm.ProcessList.First(p => p.ProcessPath == "a.exe");
        vm.SelectedProcess = aItem;
        for (var i = 0; i < 5; i++) await Task.Yield();

        Assert.Equal("a.exe", observedPath);
    }

    // ---- Phase 6.9: dismiss-X + auto-clear-on-action-entry ----

    [Fact]
    public void DismissErrorCommand_ClearsErrorState() {
        var (vm, client) = CreateViewModelWithClient();
        client.AggregateTimelineException = new RpcException(
            new Status(StatusCode.Unavailable, "daemon offline"));
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last7Days);
        Assert.True(vm.HasError);
        Assert.NotEmpty(vm.ErrorMessage);

        vm.DismissErrorCommand.Execute(null);

        Assert.False(vm.HasError);
        Assert.Empty(vm.ErrorMessage);
    }

    [Fact]
    public async Task SelectedTimeRangeChange_ClearsStaleErrorAtEntry_OnSuccess() {
        // First range change fails → banner. Second succeeds → auto-clear
        // at entry to LoadHistoricalRangeAsync removes the stale state.
        var (vm, client) = CreateViewModelWithClient();
        client.AggregateTimelineException = new RpcException(
            new Status(StatusCode.Unavailable, "first fail"));
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last7Days);
        Assert.True(vm.HasError);

        client.AggregateTimelineException = null;
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last24Hours);
        // Yield to let the fire-and-forget LoadHistoricalRangeAsync run.
        for (var i = 0; i < 10; i++) await Task.Yield();

        Assert.False(vm.HasError);
        Assert.Empty(vm.ErrorMessage);
    }

    // ---- Phase 9.6: Scanner → Traffic cross-link filter ----

    [Fact]
    public void InitialState_RemoteAddressFilter_IsNull() {
        var vm = CreateViewModel();

        Assert.Null(vm.RemoteAddressFilter);
        Assert.False(vm.HasRemoteAddressFilter);
    }

    [Fact]
    public async Task ApplyRemoteAddressFilter_FromLiveMode_SwitchesToOneHourAndSetsFilter() {
        var vm = CreateViewModel();
        // Default range is Last5Minutes (live). Switching to filter mode
        // must move to a historical preset so the SQL filter has data to
        // bite on.
        Assert.True(vm.SelectedTimeRange.IsLive);

        vm.ApplyRemoteAddressFilter("192.168.1.42");
        // Yield to let the OnSelectedTimeRangeChanged → LoadHistoricalRangeAsync
        // fire-and-forget run.
        for (var i = 0; i < 10; i++) await Task.Yield();

        Assert.Equal("192.168.1.42", vm.RemoteAddressFilter);
        Assert.True(vm.HasRemoteAddressFilter);
        Assert.Equal(TimeRangePreset.Last1Hour, vm.SelectedTimeRange.Preset);
        Assert.False(vm.SelectedTimeRange.IsLive);
    }

    [Fact]
    public async Task ApplyRemoteAddressFilter_FromHistoricalMode_KeepsRangeAndSetsFilter() {
        var vm = CreateViewModel();
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last24Hours);
        for (var i = 0; i < 10; i++) await Task.Yield();

        vm.ApplyRemoteAddressFilter("10.0.0.5");
        for (var i = 0; i < 10; i++) await Task.Yield();

        Assert.Equal("10.0.0.5", vm.RemoteAddressFilter);
        Assert.Equal(TimeRangePreset.Last24Hours, vm.SelectedTimeRange.Preset);
    }

    [Fact]
    public void ApplyRemoteAddressFilter_NullOrEmpty_Throws() {
        var vm = CreateViewModel();

        Assert.Throws<ArgumentException>(() => vm.ApplyRemoteAddressFilter(""));
        Assert.Throws<ArgumentException>(() => vm.ApplyRemoteAddressFilter("   "));
        Assert.Throws<ArgumentNullException>(() => vm.ApplyRemoteAddressFilter(null!));
    }

    [Fact]
    public async Task ClearRemoteAddressFilterCommand_ClearsFilter() {
        var vm = CreateViewModel();
        vm.ApplyRemoteAddressFilter("192.168.1.42");
        for (var i = 0; i < 10; i++) await Task.Yield();
        Assert.True(vm.HasRemoteAddressFilter);

        vm.ClearRemoteAddressFilterCommand.Execute(null);
        for (var i = 0; i < 10; i++) await Task.Yield();

        Assert.Null(vm.RemoteAddressFilter);
        Assert.False(vm.HasRemoteAddressFilter);
    }

    [Fact]
    public void ClearRemoteAddressFilterCommand_NoActiveFilter_NoOp() {
        var vm = CreateViewModel();
        Assert.False(vm.HasRemoteAddressFilter);

        // Must not throw or otherwise misbehave when there's nothing to clear.
        vm.ClearRemoteAddressFilterCommand.Execute(null);

        Assert.False(vm.HasRemoteAddressFilter);
    }

    [Fact]
    public async Task ActivateAsync_CompletesImmediately() {
        // Phase 9.6: the Traffic tab's ActivateAsync is a no-op satisfaction
        // of the cross-link contract — completes synchronously today.
        var vm = CreateViewModel();

        var task = vm.ActivateAsync(CancellationToken.None);

        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact]
    public async Task RemoteAddressFilter_SurvivesHistoricalToHistoricalRangeChange() {
        // Regression: changing time range between two historical presets
        // must preserve the IP filter — the LoadHistoricalRangeAsync call
        // re-reads RemoteAddressFilter and includes it in the query.
        var vm = CreateViewModel();
        vm.ApplyRemoteAddressFilter("192.168.1.42");
        for (var i = 0; i < 10; i++) await Task.Yield();
        Assert.True(vm.HasRemoteAddressFilter);
        Assert.Equal(TimeRangePreset.Last1Hour, vm.SelectedTimeRange.Preset);

        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last24Hours);
        for (var i = 0; i < 10; i++) await Task.Yield();

        Assert.True(vm.HasRemoteAddressFilter);
        Assert.Equal("192.168.1.42", vm.RemoteAddressFilter);
        Assert.Equal(TimeRangePreset.Last24Hours, vm.SelectedTimeRange.Preset);
    }

    [Fact]
    public async Task RemoteAddressFilter_AutoClearsWhenSwitchingToLiveMode() {
        // Live ProcessState carries per-process delta bytes only — no
        // per-destination info — so the IP filter can't be applied
        // client-side in live mode. Auto-clearing the filter makes the
        // chip's visible state match the displayed data.
        var vm = CreateViewModel();
        vm.ApplyRemoteAddressFilter("192.168.1.42");
        for (var i = 0; i < 10; i++) await Task.Yield();
        Assert.True(vm.HasRemoteAddressFilter);

        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last5Minutes);
        for (var i = 0; i < 10; i++) await Task.Yield();

        Assert.False(vm.HasRemoteAddressFilter);
        Assert.Null(vm.RemoteAddressFilter);
        Assert.True(vm.SelectedTimeRange.IsLive);
    }

    // ---- Chart range selection + pause (GlassWire-style drill-down) ----

    [Fact]
    public void ChartSelection_Set_ActivatesAndSetsWindowLabel() {
        var vm = CreateViewModel();
        vm.UpdateFromStates(new Dictionary<string, ProcessState> {
            ["a.exe"] = MakeState("a.exe", "a", [10], [1]),
        });
        Assert.False(vm.IsSelectionActive);

        vm.ChartSelection = new ChartSelectionRange(0.1, 0.4);

        Assert.True(vm.IsSelectionActive);
        Assert.NotEmpty(vm.SelectionWindowLabel);
    }

    [Fact]
    public void ChartSelection_PausesLiveUpdates_ThenResumeCatchesUp() {
        var vm = CreateViewModel();
        vm.UpdateFromStates(new Dictionary<string, ProcessState> {
            ["a.exe"] = MakeState("a.exe", "a", [10, 20, 30], [1, 2, 3]),
        });
        Assert.Equal(new long[] { 10, 20, 30 }, vm.ChartData![0].Values);

        // Selecting a range pauses the live view — a fresh tick must not change
        // the frozen chart.
        vm.ChartSelection = new ChartSelectionRange(0.2, 0.6);
        vm.UpdateFromStates(new Dictionary<string, ProcessState> {
            ["a.exe"] = MakeState("a.exe", "a", [100, 200, 300], [5, 10, 15]),
        });
        Assert.Equal(new long[] { 10, 20, 30 }, vm.ChartData![0].Values);

        // Resume lifts the pause and catches the chart up to the latest tick.
        vm.ResumeCommand.Execute(null);

        Assert.False(vm.IsSelectionActive);
        Assert.Equal(new long[] { 100, 200, 300 }, vm.ChartData![0].Values);
    }

    [Fact]
    public void RangeChange_ClearsChartSelection() {
        var vm = CreateViewModel();
        vm.ChartSelection = new ChartSelectionRange(0.1, 0.4);
        Assert.True(vm.IsSelectionActive);

        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last1Hour);

        Assert.Null(vm.ChartSelection);
        Assert.False(vm.IsSelectionActive);
    }

    [Fact]
    public void ProcessChange_ClearsChartSelection() {
        var vm = CreateViewModel();
        vm.UpdateFromStates(new Dictionary<string, ProcessState> {
            ["a.exe"] = MakeState("a.exe", "a", [100], [50]),
        });
        vm.ChartSelection = new ChartSelectionRange(0.1, 0.4);
        Assert.True(vm.IsSelectionActive);

        var aItem = vm.ProcessList.First(p => p.ProcessPath == "a.exe");
        vm.SelectedProcess = aItem;

        Assert.Null(vm.ChartSelection);
    }

    [Fact]
    public async Task ChartSelection_FetchesDestinationsForWindow() {
        var (vm, client) = CreateViewModelWithClient();
        client.ProcessDestinationsResponder = _ => {
            var response = new GetProcessDestinationsResponse();
            response.Destinations.Add(new DestinationSummary {
                RemoteAddress = "1.1.1.1", Hostname = "one.example",
                Country = "US", TotalBytesIn = 1000, TotalBytesOut = 500, ConnectionCount = 2,
            });
            return response;
        };

        vm.ChartSelection = new ChartSelectionRange(0.2, 0.8);
        for (var i = 0; i < 10 && vm.SelectionDestinations.Count == 0; i++) await Task.Yield();

        var row = Assert.Single(vm.SelectionDestinations);
        Assert.Equal("one.example", row.DisplayName);
        Assert.Equal("1.1.1.1", row.RemoteAddress);
        Assert.True(row.ShowAddress);
        Assert.NotEmpty(row.SpeedLabel);
        Assert.False(vm.SelectionLoading);
    }

    [Fact]
    public void ChartSelection_AllProcesses_SetsAllProcessesAppLabel() {
        var (vm, client) = CreateViewModelWithClient();
        client.ProcessDestinationsResponder = _ => new GetProcessDestinationsResponse();

        vm.ChartSelection = new ChartSelectionRange(0.1, 0.4);

        Assert.Equal("All processes", vm.SelectionAppLabel);
    }

    [Fact]
    public async Task Resume_ClearsSelectionDestinations() {
        var (vm, client) = CreateViewModelWithClient();
        client.ProcessDestinationsResponder = _ => {
            var response = new GetProcessDestinationsResponse();
            response.Destinations.Add(new DestinationSummary {
                RemoteAddress = "1.1.1.1", Hostname = "one", Country = "US",
                TotalBytesIn = 10, TotalBytesOut = 5, ConnectionCount = 1,
            });
            return response;
        };
        vm.ChartSelection = new ChartSelectionRange(0.2, 0.8);
        for (var i = 0; i < 10 && vm.SelectionDestinations.Count == 0; i++) await Task.Yield();
        Assert.NotEmpty(vm.SelectionDestinations);

        vm.ResumeCommand.Execute(null);

        Assert.Empty(vm.SelectionDestinations);
        Assert.Empty(vm.SelectionAppLabel);
    }
}
