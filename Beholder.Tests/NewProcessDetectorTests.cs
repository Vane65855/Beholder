using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Pipeline;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public sealed class NewProcessDetectorTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FirstFlow_NewBinary_RegistersAndEmitsAlert() {
        var fixture = new Fixture();

        await fixture.Detector.ProcessAsync(@"C:\bin\app.exe", CancellationToken.None);

        var emission = Assert.Single(fixture.Emitter.Emissions);
        Assert.Equal(AlertKind.NewProcess, emission.Kind);
        Assert.Equal(@"C:\bin\app.exe", emission.ProcessPath);
        Assert.Equal("app.exe accessed the network for the first time", emission.Summary);

        var info = await fixture.Registry.GetByPathAsync(@"C:\bin\app.exe", CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal("app.exe", info.DisplayName);
        Assert.Null(info.Sha256);   // first hash arrives via BinaryHashMonitor
        Assert.Equal(FixedTimestamp, info.FirstSeen);
        Assert.Equal(FixedTimestamp, info.LastSeen);
    }

    [Fact]
    public async Task FirstFlow_AlreadyRegistered_NoAlert_RefreshesLastSeen() {
        // Daemon-restart case: the engine forgot the path but the registry
        // remembers. Must not re-alert; must update last_seen.
        var fixture = new Fixture();
        var existing = new ProcessInfo(
            path: @"C:\bin\app.exe",
            displayName: "app.exe",
            sha256: new byte[] { 0xAA, 0xBB, 0xCC },
            firstSeen: FixedTimestamp.AddDays(-1),
            lastSeen: FixedTimestamp.AddDays(-1),
            lastHashedAt: FixedTimestamp.AddDays(-1));
        await fixture.Registry.RegisterAsync(existing, CancellationToken.None);

        await fixture.Detector.ProcessAsync(@"C:\bin\app.exe", CancellationToken.None);

        Assert.Empty(fixture.Emitter.Emissions);
        var refreshed = await fixture.Registry.GetByPathAsync(@"C:\bin\app.exe", CancellationToken.None);
        Assert.NotNull(refreshed);
        Assert.Equal(FixedTimestamp.AddDays(-1), refreshed.FirstSeen);  // immutable
        Assert.Equal(FixedTimestamp, refreshed.LastSeen);                // refreshed
        Assert.NotNull(refreshed.Sha256);                                 // preserved
    }

    [Fact]
    public async Task FirstFlow_DetectionDisabled_NoAlert_NoRegistration() {
        var fixture = new Fixture();
        fixture.Options.Set(new AlertOptions { EnableNewProcessDetection = false });

        await fixture.Detector.ProcessAsync(@"C:\bin\app.exe", CancellationToken.None);

        Assert.Empty(fixture.Emitter.Emissions);
        var info = await fixture.Registry.GetByPathAsync(@"C:\bin\app.exe", CancellationToken.None);
        Assert.Null(info);
    }

    [Fact]
    public async Task EmitterFailure_Logged_DoesNotCrashDetector() {
        var fixture = new Fixture();
        fixture.Emitter.Exception = new InvalidOperationException("boom");

        // Must not throw — detector swallows + logs per-event errors so one
        // bad path doesn't take down the loop.
        await fixture.Detector.ProcessAsync(@"C:\bin\app.exe", CancellationToken.None);

        // The path was registered before the emit attempt, so the registry
        // entry exists; the emit failure suppressed the alert row.
        Assert.Empty(fixture.Emitter.Emissions);
        var info = await fixture.Registry.GetByPathAsync(@"C:\bin\app.exe", CancellationToken.None);
        Assert.NotNull(info);
    }

    [Fact]
    public async Task StartAsync_SubscribesToFlowSource_EmitsOnRaise() {
        var fixture = new Fixture();

        await fixture.Detector.StartAsync(CancellationToken.None);
        Assert.True(fixture.FlowSource.HasSubscribers);

        // Raise the event and synchronously walk the same path the
        // fire-and-forget Task takes.
        await fixture.Detector.ProcessAsync(@"C:\bin\app.exe", CancellationToken.None);
        Assert.Single(fixture.Emitter.Emissions);

        await fixture.Detector.StopAsync(CancellationToken.None);
        Assert.False(fixture.FlowSource.HasSubscribers);
    }

    [Fact]
    public void Constructor_NullFlowSource_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new NewProcessDetector(
            flowSource: null!,
            processRegistry: new FakeProcessRegistry(),
            alertEmitter: new FakeAlertEmitter(),
            options: new FakeOptionsMonitor<AlertOptions>(new AlertOptions()),
            timeProvider: new FakeTimeProvider(FixedTimestamp),
            logger: NullLogger<NewProcessDetector>.Instance));

    private sealed class Fixture {
        public FakeProcessFirstNetworkFlowSource FlowSource { get; } = new();
        public FakeProcessRegistry Registry { get; } = new();
        public FakeAlertEmitter Emitter { get; } = new();
        public FakeOptionsMonitor<AlertOptions> Options { get; } = new(new AlertOptions());
        public FakeTimeProvider Time { get; } = new(FixedTimestamp);
        public NewProcessDetector Detector { get; }

        public Fixture() {
            Detector = new NewProcessDetector(
                FlowSource, Registry, Emitter, Options, Time,
                NullLogger<NewProcessDetector>.Instance);
        }
    }
}
