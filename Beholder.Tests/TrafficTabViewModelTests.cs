using Beholder.Ui.Services;
using Beholder.Ui.ViewModels;
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
}
