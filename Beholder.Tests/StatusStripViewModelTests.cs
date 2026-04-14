using Beholder.Protocol.Local;
using Beholder.Ui.Services;
using Beholder.Ui.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public class StatusStripViewModelTests {
    private static (StatusStripViewModel Vm, ProcessStateService Service) CreateViewModelWithService() {
        var fakeClient = new FakeDaemonClient();
        var subscriber = new DaemonStreamSubscriber(
            fakeClient,
            NullLogger<DaemonStreamSubscriber>.Instance);
        var service = new ProcessStateService(subscriber);
        var vm = new StatusStripViewModel(service);
        return (vm, service);
    }

    private static Dictionary<string, ProcessState> MakeStates(
        params (string path, string name, long totalIn, long totalOut, long deltaIn, long deltaOut)[] entries) {
        var dict = new Dictionary<string, ProcessState>(StringComparer.Ordinal);
        foreach (var (path, name, totalIn, totalOut, deltaIn, deltaOut) in entries) {
            dict[path] = new ProcessState {
                ProcessPath = path,
                DisplayName = name,
                TotalBytesIn = totalIn,
                TotalBytesOut = totalOut,
                DeltaBytesIn = deltaIn,
                DeltaBytesOut = deltaOut,
                LastSeen = DateTimeOffset.UtcNow,
            };
        }
        return dict;
    }

    [Fact]
    public void Ctor_NullService_Throws() =>
        Assert.Throws<ArgumentNullException>("processStateService",
            () => new StatusStripViewModel(null!));

    [Fact]
    public void UpdateFromStates_AggregatesAcrossProcesses_UpdatesTotals() {
        var (vm, _) = CreateViewModelWithService();
        var states = MakeStates(
            ("fake/firefox.exe", "firefox.exe", 1000, 2000, 100, 200),
            ("fake/chrome.exe", "chrome.exe", 3000, 4000, 300, 400));

        vm.UpdateFromStates(states);

        Assert.Equal("5.9 KB", vm.OutboundTotalLabel);
        Assert.Equal("3.9 KB", vm.InboundTotalLabel);
        Assert.Equal("9.8 KB", vm.WanTotalLabel);
    }

    [Fact]
    public void UpdateFromStates_UpdatesRateLabels() {
        var (vm, _) = CreateViewModelWithService();
        var states = MakeStates(("fake/test.exe", "test.exe", 0, 0, 2048, 1024));

        vm.UpdateFromStates(states);

        Assert.Equal("1.0 KB/s", vm.OutboundRateLabel);
        Assert.Equal("2.0 KB/s", vm.InboundRateLabel);
    }

    [Fact]
    public void UpdateFromStates_IdleState_HasTrafficIsFalse() {
        var (vm, _) = CreateViewModelWithService();
        var states = MakeStates(("fake/test.exe", "test.exe", 0, 0, 0, 0));

        vm.UpdateFromStates(states);

        Assert.False(vm.HasTraffic);
    }

    [Fact]
    public void UpdateFromStates_OutboundHeavy_OutboundRatioHigher() {
        var (vm, _) = CreateViewModelWithService();
        var states = MakeStates(("fake/test.exe", "test.exe", 0, 0, 100, 900));

        vm.UpdateFromStates(states);

        Assert.True(vm.HasTraffic);
        Assert.True(vm.OutboundRatio > vm.InboundRatio);
    }

    [Fact]
    public void UpdateFromStates_InboundHeavy_InboundRatioHigher() {
        var (vm, _) = CreateViewModelWithService();
        var states = MakeStates(("fake/test.exe", "test.exe", 0, 0, 900, 100));

        vm.UpdateFromStates(states);

        Assert.True(vm.HasTraffic);
        Assert.True(vm.InboundRatio > vm.OutboundRatio);
    }

    [Fact]
    public void UpdateFromStates_Smoothing_DoesNotJumpInstantly() {
        var (vm, _) = CreateViewModelWithService();
        var states = MakeStates(("fake/test.exe", "test.exe", 0, 0, 0, 1000));

        vm.UpdateFromStates(states);

        // Smoothing starts from 0.5 and LERPs toward 1.0 with factor 0.3,
        // so after one tick: 0.5 * 0.7 + 1.0 * 0.3 = 0.65
        Assert.InRange(vm.OutboundRatio, 0.60, 0.70);
    }

    [Fact]
    public void UpdateFromStates_SparseBatches_RetainsTotalsFromPriorProcesses() {
        var (vm, _) = CreateViewModelWithService();

        // First update: both processes present
        var states1 = MakeStates(
            ("fake/chrome.exe", "chrome.exe", 1000, 500, 100, 50),
            ("fake/firefox.exe", "firefox.exe", 2000, 1000, 200, 100));
        vm.UpdateFromStates(states1);

        // Second update: only firefox reports, but chrome is still in state
        var states2 = MakeStates(
            ("fake/chrome.exe", "chrome.exe", 1000, 500, 0, 0),
            ("fake/firefox.exe", "firefox.exe", 2500, 1200, 500, 200));
        vm.UpdateFromStates(states2);

        Assert.Equal("3.4 KB", vm.InboundTotalLabel);
        Assert.Equal("1.7 KB", vm.OutboundTotalLabel);
    }

    [Fact]
    public void UpdateFromStates_ProcessUpdate_UpsertsNotAccumulates() {
        var (vm, _) = CreateViewModelWithService();

        var states1 = MakeStates(("fake/chrome.exe", "chrome.exe", 1000, 500, 0, 0));
        vm.UpdateFromStates(states1);

        var states2 = MakeStates(("fake/chrome.exe", "chrome.exe", 2000, 1000, 0, 0));
        vm.UpdateFromStates(states2);

        Assert.Equal("2.0 KB", vm.InboundTotalLabel);
        Assert.Equal("1000 B", vm.OutboundTotalLabel);
    }

    [Fact]
    public void UpdateFromStates_DaemonReset_ShowsNewValues() {
        var (vm, _) = CreateViewModelWithService();

        var states1 = MakeStates(("fake/chrome.exe", "chrome.exe", 50_000, 25_000, 0, 0));
        vm.UpdateFromStates(states1);

        // After daemon restart, totals drop
        var states2 = MakeStates(("fake/chrome.exe", "chrome.exe", 100, 50, 0, 0));
        vm.UpdateFromStates(states2);

        Assert.Equal("100 B", vm.InboundTotalLabel);
        Assert.Equal("50 B", vm.OutboundTotalLabel);
    }

    [Fact]
    public void UpdateFromStates_RatesReflectCurrentBatchOnly() {
        var (vm, _) = CreateViewModelWithService();

        // Only one process with specific deltas
        var states = MakeStates(("fake/firefox.exe", "firefox.exe", 0, 0, 100, 50));
        vm.UpdateFromStates(states);

        Assert.Equal("100 B/s", vm.InboundRateLabel);
        Assert.Equal("50 B/s", vm.OutboundRateLabel);
    }

    [Fact]
    public void UpdateFromStates_EmptyStates_RetainsZeros() {
        var (vm, _) = CreateViewModelWithService();

        var states1 = MakeStates(("fake/chrome.exe", "chrome.exe", 1000, 500, 100, 50));
        vm.UpdateFromStates(states1);

        var emptyStates = new Dictionary<string, ProcessState>();
        vm.UpdateFromStates(emptyStates);

        // Empty states = no processes = all zeros
        Assert.Equal("0 B", vm.InboundTotalLabel);
        Assert.Equal("0 B", vm.OutboundTotalLabel);
        Assert.Equal("0 B/s", vm.InboundRateLabel);
        Assert.Equal("0 B/s", vm.OutboundRateLabel);
    }
}
