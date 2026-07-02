using System.Reflection;
using Beholder.Protocol.Local;
using Beholder.Tests.TestDoubles;
using Beholder.Ui.Services;
using Beholder.Ui.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public class StatusStripViewModelTests {
    // Fixed instant used for test-data LastSeen stamps — value doesn't matter
    // for these tests (they don't assert on relative time), but fixing it
    // removes wall-clock coupling that would show as spurious flake under
    // clock skew.
    private static readonly DateTimeOffset FixedLastSeen =
        new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly BuildVersion SampleBuild = BuildVersion.Parse("0.1.1+abcdef1234567", null);

    private static ProcessStateService CreateService() {
        var fakeClient = new FakeDaemonClient();
        var subscriber = new DaemonStreamSubscriber(
            fakeClient,
            TimeProvider.System,
            NullLogger<DaemonStreamSubscriber>.Instance);
        return new ProcessStateService(subscriber, fakeClient, TimeProvider.System);
    }

    private static (StatusStripViewModel Vm, ProcessStateService Service) CreateViewModelWithService() {
        var service = CreateService();
        // SyncDispatcher runs IDispatcher.Post actions immediately on the
        // calling thread — production handlers run synchronously under test.
        var vm = new StatusStripViewModel(
            service, new SyncDispatcher(), SampleBuild, new TotalsExclusionUiState());
        return (vm, service);
    }

    /// <summary>
    /// Raises <c>ProcessStatesUpdated</c> on <paramref name="service"/> with
    /// <paramref name="states"/> — drives the same event path the daemon's
    /// live counter batch would, so the VM's
    /// <c>OnProcessStatesUpdated → IDispatcher.Post → UpdateFromStates</c>
    /// chain runs identically to production. With <see cref="SyncDispatcher"/>
    /// injected, the chain runs synchronously on the calling thread.
    /// Reflection because <c>ProcessStatesUpdated</c> is a <c>public event</c>
    /// — only the owning class can <c>Invoke</c> the backing delegate.
    /// </summary>
    private static void RaiseProcessStatesUpdated(
        ProcessStateService service, IReadOnlyDictionary<string, ProcessState> states) {
        var eventField = typeof(ProcessStateService)
            .GetField("ProcessStatesUpdated", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var del = (Action<IReadOnlyDictionary<string, ProcessState>>?)eventField.GetValue(service);
        del?.Invoke(states);
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
                LastSeen = FixedLastSeen,
            };
        }
        return dict;
    }

    [Fact]
    public void Ctor_NullService_Throws() =>
        Assert.Throws<ArgumentNullException>("processStateService",
            () => new StatusStripViewModel(
                null!, new SyncDispatcher(), SampleBuild, new TotalsExclusionUiState()));

    [Fact]
    public void Ctor_NullBuildVersion_Throws() =>
        Assert.Throws<ArgumentNullException>("buildVersion",
            () => new StatusStripViewModel(
                CreateService(), new SyncDispatcher(), null!, new TotalsExclusionUiState()));

    [Fact]
    public void UpdateFromStates_AggregatesAcrossProcesses_UpdatesTotals() {
        var (vm, service) = CreateViewModelWithService();
        var states = MakeStates(
            ("fake/firefox.exe", "firefox.exe", 1000, 2000, 100, 200),
            ("fake/chrome.exe", "chrome.exe", 3000, 4000, 300, 400));

        RaiseProcessStatesUpdated(service,states);

        Assert.Equal("5.9 KB", vm.OutboundTotalLabel);
        Assert.Equal("3.9 KB", vm.InboundTotalLabel);
        Assert.Equal("9.8 KB", vm.WanTotalLabel);
    }

    [Fact]
    public void UpdateFromStates_UpdatesRateLabels() {
        var (vm, service) = CreateViewModelWithService();
        var states = MakeStates(("fake/test.exe", "test.exe", 0, 0, 2048, 1024));

        RaiseProcessStatesUpdated(service,states);

        Assert.Equal("1.0 KB/s", vm.OutboundRateLabel);
        Assert.Equal("2.0 KB/s", vm.InboundRateLabel);
    }

    [Fact]
    public void UpdateFromStates_IdleState_HasTrafficIsFalse() {
        var (vm, service) = CreateViewModelWithService();
        var states = MakeStates(("fake/test.exe", "test.exe", 0, 0, 0, 0));

        RaiseProcessStatesUpdated(service,states);

        Assert.False(vm.HasTraffic);
    }

    [Fact]
    public void UpdateFromStates_OutboundHeavy_OutboundRatioHigher() {
        var (vm, service) = CreateViewModelWithService();
        var states = MakeStates(("fake/test.exe", "test.exe", 0, 0, 100, 900));

        RaiseProcessStatesUpdated(service,states);

        Assert.True(vm.HasTraffic);
        Assert.True(vm.OutboundRatio > vm.InboundRatio);
    }

    [Fact]
    public void UpdateFromStates_InboundHeavy_InboundRatioHigher() {
        var (vm, service) = CreateViewModelWithService();
        var states = MakeStates(("fake/test.exe", "test.exe", 0, 0, 900, 100));

        RaiseProcessStatesUpdated(service,states);

        Assert.True(vm.HasTraffic);
        Assert.True(vm.InboundRatio > vm.OutboundRatio);
    }

    [Fact]
    public void UpdateFromStates_Smoothing_DoesNotJumpInstantly() {
        var (vm, service) = CreateViewModelWithService();
        var states = MakeStates(("fake/test.exe", "test.exe", 0, 0, 0, 1000));

        RaiseProcessStatesUpdated(service,states);

        // Smoothing starts from 0.5 and LERPs toward 1.0 with factor 0.3,
        // so after one tick: 0.5 * 0.7 + 1.0 * 0.3 = 0.65
        Assert.InRange(vm.OutboundRatio, 0.60, 0.70);
    }

    [Fact]
    public void UpdateFromStates_SparseBatches_RetainsTotalsFromPriorProcesses() {
        var (vm, service) = CreateViewModelWithService();

        // First update: both processes present
        var states1 = MakeStates(
            ("fake/chrome.exe", "chrome.exe", 1000, 500, 100, 50),
            ("fake/firefox.exe", "firefox.exe", 2000, 1000, 200, 100));
        RaiseProcessStatesUpdated(service,states1);

        // Second update: only firefox reports, but chrome is still in state
        var states2 = MakeStates(
            ("fake/chrome.exe", "chrome.exe", 1000, 500, 0, 0),
            ("fake/firefox.exe", "firefox.exe", 2500, 1200, 500, 200));
        RaiseProcessStatesUpdated(service,states2);

        Assert.Equal("3.4 KB", vm.InboundTotalLabel);
        Assert.Equal("1.7 KB", vm.OutboundTotalLabel);
    }

    [Fact]
    public void UpdateFromStates_ProcessUpdate_UpsertsNotAccumulates() {
        var (vm, service) = CreateViewModelWithService();

        var states1 = MakeStates(("fake/chrome.exe", "chrome.exe", 1000, 500, 0, 0));
        RaiseProcessStatesUpdated(service,states1);

        var states2 = MakeStates(("fake/chrome.exe", "chrome.exe", 2000, 1000, 0, 0));
        RaiseProcessStatesUpdated(service,states2);

        Assert.Equal("2.0 KB", vm.InboundTotalLabel);
        Assert.Equal("1000 B", vm.OutboundTotalLabel);
    }

    [Fact]
    public void UpdateFromStates_DaemonReset_ShowsNewValues() {
        var (vm, service) = CreateViewModelWithService();

        var states1 = MakeStates(("fake/chrome.exe", "chrome.exe", 50_000, 25_000, 0, 0));
        RaiseProcessStatesUpdated(service,states1);

        // After daemon restart, totals drop
        var states2 = MakeStates(("fake/chrome.exe", "chrome.exe", 100, 50, 0, 0));
        RaiseProcessStatesUpdated(service,states2);

        Assert.Equal("100 B", vm.InboundTotalLabel);
        Assert.Equal("50 B", vm.OutboundTotalLabel);
    }

    [Fact]
    public void UpdateFromStates_RatesReflectCurrentBatchOnly() {
        var (vm, service) = CreateViewModelWithService();

        // Only one process with specific deltas
        var states = MakeStates(("fake/firefox.exe", "firefox.exe", 0, 0, 100, 50));
        RaiseProcessStatesUpdated(service,states);

        Assert.Equal("100 B/s", vm.InboundRateLabel);
        Assert.Equal("50 B/s", vm.OutboundRateLabel);
    }

    [Fact]
    public void UpdateFromStates_EmptyStates_RetainsZeros() {
        var (vm, service) = CreateViewModelWithService();

        var states1 = MakeStates(("fake/chrome.exe", "chrome.exe", 1000, 500, 100, 50));
        RaiseProcessStatesUpdated(service,states1);

        var emptyStates = new Dictionary<string, ProcessState>();
        RaiseProcessStatesUpdated(service,emptyStates);

        // Empty states = no processes = all zeros
        Assert.Equal("0 B", vm.InboundTotalLabel);
        Assert.Equal("0 B", vm.OutboundTotalLabel);
        Assert.Equal("0 B/s", vm.InboundRateLabel);
        Assert.Equal("0 B/s", vm.OutboundRateLabel);
    }

    [Fact]
    public void UpdateFromStates_TotalsExcludedProcess_SkippedFromEveryFigure() {
        var exclusions = new TotalsExclusionUiState();
        exclusions.SetExcludedPaths([@"fake/wireguard.exe"]);
        var service = CreateService();
        var vm = new StatusStripViewModel(
            service, new SyncDispatcher(), SampleBuild, exclusions);
        var states = MakeStates(
            ("fake/wireguard.exe", "wireguard", 9_000, 9_000, 900, 900),
            ("fake/firefox.exe", "firefox.exe", 1000, 2000, 100, 200));

        RaiseProcessStatesUpdated(service, states);

        // Only firefox counts: totals, rates, and the WAN sum.
        Assert.Equal("2.0 KB", vm.OutboundTotalLabel);
        Assert.Equal("1000 B", vm.InboundTotalLabel);
        Assert.Equal("200 B/s", vm.OutboundRateLabel);
        Assert.Equal("100 B/s", vm.InboundRateLabel);
        Assert.Equal("2.9 KB", vm.WanTotalLabel);
    }

    [Fact]
    public void Dispose_DoesNotThrow() {
        // Smoke: Dispose unsubscribes from the service's ProcessStatesUpdated
        // event. The symmetry is verified by code review; this test guards that
        // the Dispose path is at least reachable without throwing.
        var (vm, service) = CreateViewModelWithService();
        var exception = Record.Exception(() => vm.Dispose());
        Assert.Null(exception);
    }
}
