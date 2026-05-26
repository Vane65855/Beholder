using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Grpc;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Scanner;
using Beholder.Tests.TestDoubles;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Local = Beholder.Protocol.Local;

namespace Beholder.Tests;

public sealed class TriggerScanRpcTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 5, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly BroadcastService _broadcaster;
    private readonly FakeTimeProvider _timeProvider = new(FixedTimestamp);

    public TriggerScanRpcTests() {
        _broadcaster = new BroadcastService(
            new FakeSnapshotBatchSource(), _timeProvider, NullLogger<BroadcastService>.Instance);
    }

    public void Dispose() {
        _broadcaster.Dispose();
    }

    [Fact]
    public async Task TriggerScan_HappyPath_ReturnsSuccessWithObservedCount() {
        var probe = new FakeLanDeviceProbe {
            Responder = _ => Task.FromResult<IReadOnlyList<LanDeviceObservation>>([
                new LanDeviceObservation(
                    Mac: "aa:bb:cc:dd:ee:01", Ip: "10.0.0.1", Hostname: null, ObservedAt: FixedTimestamp),
                new LanDeviceObservation(
                    Mac: "aa:bb:cc:dd:ee:02", Ip: "10.0.0.2", Hostname: null, ObservedAt: FixedTimestamp),
            ]),
        };
        await using var scanner = CreateScanner(probe);
        var service = CreateService(scanner);

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var response = await service.TriggerScan(new Local.TriggerScanRequest(), context);

        Assert.True(response.Success);
        Assert.Equal(2, response.DevicesObserved);
        Assert.Contains("2 devices", response.Message);
    }

    [Fact]
    public async Task TriggerScan_NoProbeRegistered_ReturnsSuccessFalseWithMessage() {
        // Inactive scanner — probe is null. RunOnceManuallyAsync throws
        // InvalidOperationException which the RPC handler converts to
        // success=false rather than RpcException.
        await using var scanner = CreateScanner(probe: null);
        var service = CreateService(scanner);

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var response = await service.TriggerScan(new Local.TriggerScanRequest(), context);

        Assert.False(response.Success);
        Assert.Equal(0, response.DevicesObserved);
        Assert.Contains("inactive", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TriggerScan_ProbeThrows_ReturnsSuccessFalseWithMessage() {
        var probe = new FakeLanDeviceProbe {
            Responder = _ => throw new InvalidOperationException("simulated probe failure"),
        };
        await using var scanner = CreateScanner(probe);
        var service = CreateService(scanner);

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var response = await service.TriggerScan(new Local.TriggerScanRequest(), context);

        Assert.False(response.Success);
        Assert.Equal(0, response.DevicesObserved);
        Assert.Contains("simulated probe failure", response.Message);
    }

    [Fact]
    public async Task TriggerScan_OuterCancellation_PropagatesAsOperationCanceled() {
        // Caller-side cancellation should NOT be converted to success=false —
        // the RPC handler re-throws so gRPC can surface StatusCode.Cancelled.
        using var cts = new CancellationTokenSource();
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new FakeLanDeviceProbe {
            Responder = async ct => {
                probeStarted.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return [];
            },
        };
        await using var scanner = CreateScanner(probe);
        var service = CreateService(scanner);

        var context = new FakeServerCallContext(cts.Token);
        var triggerTask = service.TriggerScan(new Local.TriggerScanRequest(), context);

        // Cancel after the probe is actually mid-flight.
        await probeStarted.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => triggerTask);
    }

    [Fact]
    public async Task TriggerScan_ConcurrentCalls_SerializeViaScanGate() {
        // Two TriggerScan invocations against the same scanner should run
        // sequentially via _scanGate (SemaphoreSlim). The probe records the
        // peak number of concurrent in-flight Scan calls; with the gate it
        // must stay at 1.
        var probeStarted = new SemaphoreSlim(0, 100);
        var releaseProbe = new SemaphoreSlim(0, 100);
        var inFlight = 0;
        var maxInFlight = 0;
        var lockObj = new object();

        var probe = new FakeLanDeviceProbe {
            Responder = async ct => {
                lock (lockObj) {
                    inFlight++;
                    maxInFlight = Math.Max(maxInFlight, inFlight);
                }
                probeStarted.Release();
                await releaseProbe.WaitAsync(ct);
                lock (lockObj) {
                    inFlight--;
                }
                return [];
            },
        };
        await using var scanner = CreateScanner(probe);
        var service = CreateService(scanner);

        var context = new FakeServerCallContext(TestContext.Current.CancellationToken);
        var t1 = service.TriggerScan(new Local.TriggerScanRequest(), context);
        var t2 = service.TriggerScan(new Local.TriggerScanRequest(), context);

        // Wait until at least the first probe entered. Release one slot so
        // the first scan can complete, then the second one starts.
        await probeStarted.WaitAsync(TestContext.Current.CancellationToken);
        releaseProbe.Release();
        await probeStarted.WaitAsync(TestContext.Current.CancellationToken);
        releaseProbe.Release();

        await Task.WhenAll(t1, t2);

        Assert.True((await t1).Success);
        Assert.True((await t2).Success);
        Assert.Equal(1, maxInFlight);  // never more than one scan in flight
    }

    // --- Helpers ---

    private LanScannerService CreateScanner(FakeLanDeviceProbe? probe) {
        var store = new FakeLanDeviceStore();
        return new LanScannerService(
            store: store,
            vendorLookup: new FakeOuiVendorLookup(),
            eventStore: new FakeEventStore(),
            broadcaster: _broadcaster,
            options: new FakeOptionsMonitor<ScannerOptions>(new ScannerOptions { ScanIntervalSeconds = 300 }),
            timeProvider: _timeProvider,
            logger: NullLogger<LanScannerService>.Instance,
            probe: probe);
    }

    private BeholderLocalService CreateService(LanScannerService scanner) {
        var pipeline = new FlowEventPipeline(
            new FakeFlowSource(), _timeProvider,
            new FakeTrafficStore(), new FakeDnsCacheStore(), new FakeDnsCache(),
            new FakeOptionsMonitor<TrafficStorageOptions>(new TrafficStorageOptions()),
            new FakeRecordingSettingsState(),
            NullLogger<FlowEventPipeline>.Instance, NullLoggerFactory.Instance);
        return new BeholderLocalService(
            _broadcaster, pipeline,
            new FakeFirewallRuleStore(), new FakeAlertStore(),
            new FakeFirewallController(), new FakeFirewallEnforcementState(),
            new FakeEventStore(), new FakeTrafficStore(),
            new FakeLanDeviceStore(), scanner,
            new FakeChainStatusCache(), new FakeStorageStatsProvider(),
            new FakeRecordingSettingsState(), new FakeHostnameResolutionSettingsState(),
            new FakeAlertSettingsState(),
            new FakeScannerSettingsState(),
            new FakeSettingsOverridesStore(),
            _timeProvider, NullLogger<BeholderLocalService>.Instance);
    }
}
