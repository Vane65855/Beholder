using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Scanner;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

/// <summary>
/// Shared factories for constructing daemon-side services in tests with
/// minimal boilerplate. Each method returns a service configured for the
/// "inactive / no-op" path so RPC tests that don't exercise the scanner
/// pipeline can still satisfy <see cref="BeholderLocalService"/>'s constructor
/// dependencies without per-test setup.
/// </summary>
internal static class TestServiceFactory {
    /// <summary>
    /// Creates a <see cref="LanScannerService"/> with no probe registered.
    /// In this state the service exists for DI satisfaction only — its scan
    /// loop never runs and <see cref="LanScannerService.RunOnceManuallyAsync"/>
    /// throws <see cref="InvalidOperationException"/>. RPC tests that don't
    /// touch <c>TriggerScan</c> can pass this without further setup.
    /// </summary>
    public static LanScannerService CreateInactiveLanScannerService(
        ILanDeviceStore? store = null,
        IEventStore? eventStore = null,
        BroadcastService? broadcaster = null,
        TimeProvider? timeProvider = null
    ) {
        var tp = timeProvider ?? new FakeTimeProvider();
        return new LanScannerService(
            store: store ?? new FakeLanDeviceStore(),
            vendorLookup: new FakeOuiVendorLookup(),
            eventStore: eventStore ?? new FakeEventStore(),
            broadcaster: broadcaster ?? new BroadcastService(
                new FakeSnapshotBatchSource(),
                tp,
                NullLogger<BroadcastService>.Instance),
            options: new FakeOptionsMonitor<ScannerOptions>(new ScannerOptions()),
            timeProvider: tp,
            logger: NullLogger<LanScannerService>.Instance,
            probe: null);
    }
}
