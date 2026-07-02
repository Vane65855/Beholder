using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using Beholder.Ui.ViewModels;

namespace Beholder.Tests;

// Tests for ProcessListCoordinator's "unknown"-sentinel filter, parallel to
// FirewallTabViewModel.IsExcludedProcess (which filters the same sentinel
// from the Firewall tab's rule table). Both filters address the same root
// cause — ProcessPathResolver in Beholder.Daemon.Windows emits ("unknown",
// "unknown") when a PID has already exited — but the Traffic tab's filter
// keeps "System" (visibility surface, kernel traffic is real diagnostic
// data) where the Firewall tab's filter excludes it (rule surface, no rule
// can target the kernel pseudo-process).
public class ProcessListCoordinatorTests {
    private static ProcessState MakeState(
        string path, string name, long[] recentIn, long[] recentOut) {
        var state = new ProcessState { ProcessPath = path, DisplayName = name };
        foreach (var v in recentIn) state.RecentDeltaIn.Add(v);
        foreach (var v in recentOut) state.RecentDeltaOut.Add(v);
        return state;
    }

    [Fact]
    public void Upsert_FiltersUnknownFromProcessList() {
        // Live path: ProcessStateService.ProcessStatesUpdated → Upsert. The
        // "unknown" sentinel must not appear as a row, and its bytes must not
        // contribute to the "All processes" aggregate (otherwise the visible
        // rows wouldn't sum to the aggregate).
        var coordinator = new ProcessListCoordinator(new TotalsExclusionUiState());
        var states = new Dictionary<string, ProcessState> {
            ["unknown"] = MakeState("unknown", "unknown", [100, 200, 300], [10, 20, 30]),
            [@"C:\bin\firefox.exe"] = MakeState(@"C:\bin\firefox.exe", "firefox.exe", [1, 2, 3], [4, 5, 6]),
        };

        coordinator.Upsert(states);

        // List = [AllProcessesItem, firefox] — exactly 2 items, no "unknown".
        Assert.Equal(2, coordinator.List.Count);
        Assert.True(coordinator.List[0].IsAll);
        Assert.Equal(@"C:\bin\firefox.exe", coordinator.List[1].ProcessPath);

        // Aggregate row carries firefox's bytes only, not unknown's.
        // firefox: recentIn=1+2+3=6, recentOut=4+5+6=15
        Assert.Equal(6, coordinator.AllProcessesItem.RecentBytesIn);
        Assert.Equal(15, coordinator.AllProcessesItem.RecentBytesOut);
    }

    [Fact]
    public void ApplyHistorical_FiltersUnknownFromProcessList() {
        // Historical path: GetProcessSummariesAsync → ApplyHistorical (on
        // range-change). Same filter semantics as Upsert.
        var coordinator = new ProcessListCoordinator(new TotalsExclusionUiState());
        var summaries = new List<ProcessTrafficSummaryProto> {
            new() {
                ProcessPath = "unknown",
                ProcessName = "unknown",
                TotalBytesIn = 500_000,
                TotalBytesOut = 400_000,
            },
            new() {
                ProcessPath = @"C:\bin\firefox.exe",
                ProcessName = "firefox.exe",
                TotalBytesIn = 1_000,
                TotalBytesOut = 2_000,
            },
        };

        coordinator.ApplyHistorical(summaries);

        Assert.Equal(2, coordinator.List.Count);
        Assert.True(coordinator.List[0].IsAll);
        Assert.Equal(@"C:\bin\firefox.exe", coordinator.List[1].ProcessPath);

        // Aggregate excludes the 500k/400k from "unknown".
        Assert.Equal(1_000, coordinator.AllProcessesItem.RecentBytesIn);
        Assert.Equal(2_000, coordinator.AllProcessesItem.RecentBytesOut);
    }

    [Fact]
    public void Upsert_DoesNotFilterPathContainingUnknown() {
        // Scope-pinning test: only the literal sentinel "unknown" is filtered.
        // Real applications with the substring in their path — e.g., a
        // deliberately-named "unknown.exe" or a publisher folder named
        // "unknown-publisher" — must appear normally. The filter compares the
        // full path with Ordinal equality (string.Equals, not Contains/
        // EndsWith) precisely to make this distinction; this test guards
        // against an accidental refactor that broadens the match. Mirrors
        // ActivateAsync_DoesNotFilterPathContainingUnknown for the Firewall
        // tab.
        var coordinator = new ProcessListCoordinator(new TotalsExclusionUiState());
        var states = new Dictionary<string, ProcessState> {
            [@"C:\bin\unknown.exe"] = MakeState(@"C:\bin\unknown.exe", "unknown.exe", [10], [20]),
            [@"C:\unknown-publisher\app.exe"] = MakeState(@"C:\unknown-publisher\app.exe", "app.exe", [30], [40]),
        };

        coordinator.Upsert(states);

        // Both paths contain the substring "unknown" but neither is the
        // sentinel itself; both must survive the filter.
        Assert.Equal(3, coordinator.List.Count);  // AllProcessesItem + 2 real rows
        Assert.Contains(coordinator.List, p => p.ProcessPath == @"C:\bin\unknown.exe");
        Assert.Contains(coordinator.List, p => p.ProcessPath == @"C:\unknown-publisher\app.exe");
    }

    // ---- Totals exclusions ("Exclude from totals") ----

    [Fact]
    public void Upsert_TotalsExcludedProcess_HiddenAndNotCounted() {
        var exclusions = new TotalsExclusionUiState();
        exclusions.SetExcludedPaths([@"C:\vpn\wireguard.exe"]);
        var coordinator = new ProcessListCoordinator(exclusions);
        var states = new Dictionary<string, ProcessState> {
            [@"C:\vpn\wireguard.exe"] = MakeState(@"C:\vpn\wireguard.exe", "wireguard", [1000], [900]),
            [@"C:\bin\firefox.exe"] = MakeState(@"C:\bin\firefox.exe", "firefox.exe", [10], [20]),
        };

        coordinator.Upsert(states);

        // Hidden by default: only [All, firefox]; all-row carries firefox only.
        Assert.Equal(2, coordinator.List.Count);
        Assert.Equal(@"C:\bin\firefox.exe", coordinator.List[1].ProcessPath);
        Assert.Equal(10, coordinator.AllProcessesItem.RecentBytesIn);
        Assert.Equal(20, coordinator.AllProcessesItem.RecentBytesOut);
    }

    [Fact]
    public void Upsert_ShowExcludedOn_RowVisibleWithMarkerButStillNotCounted() {
        var exclusions = new TotalsExclusionUiState();
        exclusions.SetExcludedPaths([@"C:\vpn\wireguard.exe"]);
        exclusions.SetShowExcluded(true);
        var coordinator = new ProcessListCoordinator(exclusions);
        var states = new Dictionary<string, ProcessState> {
            [@"C:\vpn\wireguard.exe"] = MakeState(@"C:\vpn\wireguard.exe", "wireguard", [1000], [900]),
            [@"C:\bin\firefox.exe"] = MakeState(@"C:\bin\firefox.exe", "firefox.exe", [10], [20]),
        };

        coordinator.Upsert(states);

        Assert.Equal(3, coordinator.List.Count);
        var vpnRow = coordinator.List.First(i => i.ProcessPath == @"C:\vpn\wireguard.exe");
        Assert.True(vpnRow.IsExcludedFromTotals);
        Assert.False(coordinator.List.First(i => i.ProcessPath == @"C:\bin\firefox.exe").IsExcludedFromTotals);
        // Marker or not, the all-row never counts the excluded process.
        Assert.Equal(10, coordinator.AllProcessesItem.RecentBytesIn);
        Assert.Equal(20, coordinator.AllProcessesItem.RecentBytesOut);
    }

    [Fact]
    public void Upsert_ExclusionAddedLater_RemovesExistingRow() {
        var exclusions = new TotalsExclusionUiState();
        var coordinator = new ProcessListCoordinator(exclusions);
        var states = new Dictionary<string, ProcessState> {
            [@"C:\vpn\wireguard.exe"] = MakeState(@"C:\vpn\wireguard.exe", "wireguard", [1000], [900]),
        };
        coordinator.Upsert(states);
        Assert.Equal(2, coordinator.List.Count);

        exclusions.SetExcludedPaths([@"C:\vpn\wireguard.exe"]);
        coordinator.Upsert(states);

        var remaining = Assert.Single(coordinator.List);
        Assert.True(remaining.IsAll);
        Assert.Equal(0, coordinator.AllProcessesItem.RecentBytesIn);
    }

    [Fact]
    public void ApplyHistorical_TotalsExcludedProcess_HiddenAndNotCounted() {
        var exclusions = new TotalsExclusionUiState();
        exclusions.SetExcludedPaths([@"C:\vpn\wireguard.exe"]);
        var coordinator = new ProcessListCoordinator(exclusions);
        var summaries = new List<ProcessTrafficSummaryProto> {
            new() { ProcessPath = @"C:\vpn\wireguard.exe", ProcessName = "wireguard", TotalBytesIn = 9_000, TotalBytesOut = 8_000 },
            new() { ProcessPath = @"C:\bin\firefox.exe", ProcessName = "firefox.exe", TotalBytesIn = 1_000, TotalBytesOut = 2_000 },
        };

        coordinator.ApplyHistorical(summaries);

        Assert.Equal(2, coordinator.List.Count);
        Assert.Equal(1_000, coordinator.AllProcessesItem.RecentBytesIn);
        Assert.Equal(2_000, coordinator.AllProcessesItem.RecentBytesOut);
    }
}
