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
        fixture.AlertSettings.SetSettings(
            enableNewProcessDetection: false,
            enableHashChangeDetection: true,
            enableChainIntegrityMonitor: true);

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
            alertSettings: new FakeAlertSettingsState(),
            timeProvider: new FakeTimeProvider(FixedTimestamp),
            logger: NullLogger<NewProcessDetector>.Instance));

    // ---- Phase 7.5: identity-aware dedup + spoof detection ----

    [Fact]
    public async Task FirstFlow_LogicalIdentityMatch_SamePublisher_NoAlert() {
        // Discord 9225 already in registry (from a prior session).
        // Discord auto-updates to 9235 in a sibling app-* folder. Same
        // VersionInfo, same install root, same publisher → silent.
        var fixture = new Fixture(withIdentityProvider: true);
        var oldPath = @"C:\Users\Vane\AppData\Local\Discord\app-1.0.9225\Discord.exe";
        var newPath = @"C:\Users\Vane\AppData\Local\Discord\app-1.0.9235\Discord.exe";
        var existing = new ProcessInfo(
            path: oldPath, displayName: "Discord.exe",
            sha256: null, firstSeen: FixedTimestamp.AddDays(-30),
            lastSeen: FixedTimestamp.AddDays(-1), lastHashedAt: null,
            companyName: "Discord, Inc.", productName: "Discord",
            installRoot: @"C:\Users\Vane\AppData\Local\Discord",
            certSubjectCn: "CN=Discord Inc., O=Discord Inc., L=San Francisco, S=California, C=US",
            certIssuerCn: "CN=DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1, O=DigiCert, Inc., C=US",
            signatureStatus: SignatureValidationStatus.Valid);
        await fixture.Registry.RegisterAsync(existing, CancellationToken.None);
        fixture.IdentityProvider!.Set(newPath, new BinaryIdentity(
            CompanyName: "Discord, Inc.",
            ProductName: "Discord",
            Signature: new AuthenticodeInfo(
                SubjectCn: "CN=Discord Inc., O=Discord Inc., L=San Francisco, S=California, C=US",
                IssuerCn: "CN=DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1, O=DigiCert, Inc., C=US",
                Status: SignatureValidationStatus.Valid)));

        await fixture.Detector.ProcessAsync(newPath, CancellationToken.None);

        Assert.Empty(fixture.Emitter.Emissions);  // silent — no auto-update noise
        var refreshed = await fixture.Registry.GetByPathAsync(newPath, CancellationToken.None);
        Assert.NotNull(refreshed);  // new path registered for future short-circuit
    }

    [Fact]
    public async Task FirstFlow_LogicalIdentityMatch_DifferentPublisher_FiresSpoofAlert() {
        // Same logical identity (CompanyName, ProductName, InstallRoot) but
        // a different signing publisher → SPOOF DETECTED.
        var fixture = new Fixture(withIdentityProvider: true);
        var trustedPath = @"C:\Users\Vane\AppData\Local\Discord\app-1.0.9225\Discord.exe";
        var spoofPath = @"C:\Users\Vane\AppData\Local\Discord\evil\Discord.exe";
        var trusted = new ProcessInfo(
            path: trustedPath, displayName: "Discord.exe",
            sha256: null, firstSeen: FixedTimestamp.AddDays(-30),
            lastSeen: FixedTimestamp.AddDays(-1), lastHashedAt: null,
            companyName: "Discord, Inc.", productName: "Discord",
            installRoot: @"C:\Users\Vane\AppData\Local\Discord",
            certSubjectCn: "CN=Discord Inc.",
            certIssuerCn: "CN=DigiCert Trusted G4",
            signatureStatus: SignatureValidationStatus.Valid);
        await fixture.Registry.RegisterAsync(trusted, CancellationToken.None);
        fixture.IdentityProvider!.Set(spoofPath, new BinaryIdentity(
            CompanyName: "Discord, Inc.",   // spoofed VersionInfo
            ProductName: "Discord",
            Signature: new AuthenticodeInfo(
                SubjectCn: "CN=Fake Publisher",
                IssuerCn: "CN=Some Other CA",
                Status: SignatureValidationStatus.Valid)));

        await fixture.Detector.ProcessAsync(spoofPath, CancellationToken.None);

        var emission = Assert.Single(fixture.Emitter.Emissions);
        Assert.Equal(AlertKind.HashChanged, emission.Kind);
        Assert.Equal(spoofPath, emission.ProcessPath);
        Assert.Contains("Publisher mismatch", emission.Summary);
        Assert.Contains("CN=Fake Publisher", emission.Summary);
        Assert.Contains("CN=Discord Inc.", emission.Summary);
    }

    [Fact]
    public async Task FirstFlow_DifferentInstallRoot_FiresNewProcess() {
        // Discord at AppData (trusted) vs Discord at Program Files (new
        // install). Different install roots → recognized as different
        // logical apps, fires NewProcess for the second.
        var fixture = new Fixture(withIdentityProvider: true);
        var appDataPath = @"C:\Users\Vane\AppData\Local\Discord\app-1.0.9225\Discord.exe";
        var programFilesPath = @"C:\Program Files\Discord\app-1.0.9235\Discord.exe";
        var existing = new ProcessInfo(
            path: appDataPath, displayName: "Discord.exe",
            sha256: null, firstSeen: FixedTimestamp.AddDays(-30),
            lastSeen: FixedTimestamp.AddDays(-1), lastHashedAt: null,
            companyName: "Discord, Inc.", productName: "Discord",
            installRoot: @"C:\Users\Vane\AppData\Local\Discord",
            certSubjectCn: "CN=Discord Inc.",
            certIssuerCn: "CN=DigiCert",
            signatureStatus: SignatureValidationStatus.Valid);
        await fixture.Registry.RegisterAsync(existing, CancellationToken.None);
        fixture.IdentityProvider!.Set(programFilesPath, new BinaryIdentity(
            CompanyName: "Discord, Inc.",
            ProductName: "Discord",
            Signature: new AuthenticodeInfo(
                SubjectCn: "CN=Discord Inc.",
                IssuerCn: "CN=DigiCert",
                Status: SignatureValidationStatus.Valid)));

        await fixture.Detector.ProcessAsync(programFilesPath, CancellationToken.None);

        var emission = Assert.Single(fixture.Emitter.Emissions);
        Assert.Equal(AlertKind.NewProcess, emission.Kind);
    }

    [Fact]
    public async Task FirstFlow_NoIdentityProvider_FallsBackToPathBased() {
        // Linux daemon (no IBinaryIdentityProvider registered) — behaves
        // exactly like pre-Phase-7.5 path-based dedup.
        var fixture = new Fixture(withIdentityProvider: false);

        await fixture.Detector.ProcessAsync(@"C:\bin\app.exe", CancellationToken.None);

        var emission = Assert.Single(fixture.Emitter.Emissions);
        Assert.Equal(AlertKind.NewProcess, emission.Kind);
    }

    [Fact]
    public async Task FirstFlow_UnsignedBinary_FallsBackToPathBased() {
        // Indie tool with no Authenticode signature. Logical-identity
        // dedup requires a Valid signature, so this falls back to path-
        // based behavior — fires NewProcess as a normal first sighting.
        var fixture = new Fixture(withIdentityProvider: true);
        var path = @"C:\Users\Vane\indie-tool\indie-tool.exe";
        fixture.IdentityProvider!.Set(path, new BinaryIdentity(
            CompanyName: "indie-tool",
            ProductName: "indie-tool",
            Signature: null));   // unsigned

        await fixture.Detector.ProcessAsync(path, CancellationToken.None);

        var emission = Assert.Single(fixture.Emitter.Emissions);
        Assert.Equal(AlertKind.NewProcess, emission.Kind);
    }

    [Fact]
    public async Task FirstFlow_IdentityProviderReturnsNull_FallsBackToPathBased() {
        // Provider IS registered (Windows daemon) but ReadIdentityAsync
        // returns null — binary unreadable, PE corrupt, file vanished
        // between flow detection and the identity read. Must fall back to
        // path-based dedup, not crash. ADR 007 §Out of scope: "graceful
        // degradation when identity metadata is unavailable."
        var fixture = new Fixture(withIdentityProvider: true);
        var path = @"C:\bin\unreadable.exe";
        fixture.IdentityProvider!.Set(path, identity: null);  // provider can't read

        await fixture.Detector.ProcessAsync(path, CancellationToken.None);

        var emission = Assert.Single(fixture.Emitter.Emissions);
        Assert.Equal(AlertKind.NewProcess, emission.Kind);
        Assert.Equal(path, emission.ProcessPath);

        // Row registered with null identity columns (path is the only key).
        var info = await fixture.Registry.GetByPathAsync(path, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Null(info.CompanyName);
        Assert.Null(info.ProductName);
        Assert.Null(info.InstallRoot);
    }

    [Fact]
    public void ResolveInstallRoot_AncestorMatchesProductName_ReturnsAncestor() {
        var root = NewProcessDetector.ResolveInstallRoot(
            @"C:\Users\Vane\AppData\Local\Discord\app-1.0.9235\Discord.exe",
            "Discord");

        Assert.Equal(@"C:\Users\Vane\AppData\Local\Discord", root);
    }

    [Fact]
    public void ResolveInstallRoot_NoMatchingAncestor_ReturnsNull() {
        // svchost.exe — no folder named "Microsoft® Windows® Operating System"
        // in its ancestor chain.
        var root = NewProcessDetector.ResolveInstallRoot(
            @"C:\Windows\System32\svchost.exe",
            "Microsoft® Windows® Operating System");

        Assert.Null(root);
    }

    [Fact]
    public void ResolveInstallRoot_NullProductName_ReturnsNull() {
        var root = NewProcessDetector.ResolveInstallRoot(
            @"C:\bin\app.exe", productName: null);

        Assert.Null(root);
    }

    private sealed class Fixture {
        public FakeProcessFirstNetworkFlowSource FlowSource { get; } = new();
        public FakeProcessRegistry Registry { get; } = new();
        public FakeAlertEmitter Emitter { get; } = new();
        public FakeAlertSettingsState AlertSettings { get; } = new();
        public FakeTimeProvider Time { get; } = new(FixedTimestamp);
        public FakeBinaryIdentityProvider? IdentityProvider { get; }
        public NewProcessDetector Detector { get; }

        public Fixture(bool withIdentityProvider = false) {
            IdentityProvider = withIdentityProvider ? new FakeBinaryIdentityProvider() : null;
            Detector = new NewProcessDetector(
                FlowSource, Registry, Emitter, AlertSettings, Time,
                NullLogger<NewProcessDetector>.Instance,
                IdentityProvider);
        }
    }
}
