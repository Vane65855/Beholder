using System.Security.Cryptography;
using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Pipeline;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public sealed class BinaryHashMonitorTests : IDisposable {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public BinaryHashMonitorTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-monitor-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task SweepOnce_FirstHash_EstablishesBaseline_NoAlert() {
        var fixture = new Fixture();
        var (path, content) = await fixture.WriteBinaryAsync("app.exe", new byte[] { 0xAA });
        await fixture.RegisterAsync(path, sha256: null);  // no baseline yet

        await fixture.Monitor.SweepOnceAsync(CancellationToken.None);

        Assert.Empty(fixture.Emitter.Emissions);
        var info = await fixture.Registry.GetByPathAsync(path, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(SHA256.HashData(content), info.Sha256);
        Assert.NotNull(info.LastHashedAt);
    }

    [Fact]
    public async Task SweepOnce_HashUnchanged_NoAlert_RefreshesLastHashedAt() {
        var fixture = new Fixture();
        var (path, content) = await fixture.WriteBinaryAsync("app.exe", new byte[] { 0xBB });
        var hash = SHA256.HashData(content);
        await fixture.RegisterAsync(path, sha256: hash, lastHashedAt: FixedTimestamp.AddDays(-1));

        // Advance time so we can prove last_hash_at moves forward.
        fixture.Time.Advance(TimeSpan.FromMinutes(30));
        await fixture.Monitor.SweepOnceAsync(CancellationToken.None);

        Assert.Empty(fixture.Emitter.Emissions);
        var info = await fixture.Registry.GetByPathAsync(path, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(hash, info.Sha256);                                    // unchanged
        Assert.Equal(FixedTimestamp.AddMinutes(30), info.LastHashedAt);     // refreshed
    }

    [Fact]
    public async Task SweepOnce_HashChanged_EmitsAlert_UpdatesRegistry() {
        var fixture = new Fixture();
        var (path, _) = await fixture.WriteBinaryAsync("firefox.exe", new byte[] { 0xCC });
        var staleHash = SHA256.HashData(new byte[] { 0x00 });  // bogus prior hash
        await fixture.RegisterAsync(path, sha256: staleHash);

        await fixture.Monitor.SweepOnceAsync(CancellationToken.None);

        var emission = Assert.Single(fixture.Emitter.Emissions);
        Assert.Equal(AlertKind.HashChanged, emission.Kind);
        Assert.Equal(path, emission.ProcessPath);
        Assert.Contains("firefox.exe", emission.Summary);
        Assert.Contains("differs from prior", emission.Summary);

        var info = await fixture.Registry.GetByPathAsync(path, CancellationToken.None);
        Assert.NotNull(info);
        Assert.NotEqual(staleHash, info.Sha256);   // updated to new hash
    }

    [Fact]
    public async Task SweepOnce_HashChanged_RegistryUpdatedBeforeEmit_PreventsRepeat() {
        // If the emit fails, the registry is still updated with the new
        // hash so the next sweep doesn't re-fire on the same delta.
        var fixture = new Fixture();
        var (path, content) = await fixture.WriteBinaryAsync("app.exe", new byte[] { 0xDD });
        var staleHash = SHA256.HashData(new byte[] { 0x99 });
        await fixture.RegisterAsync(path, sha256: staleHash);
        fixture.Emitter.Exception = new InvalidOperationException("emitter down");

        // Must not throw — per-entry errors are caught.
        await fixture.Monitor.SweepOnceAsync(CancellationToken.None);

        Assert.Empty(fixture.Emitter.Emissions);
        var info = await fixture.Registry.GetByPathAsync(path, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(SHA256.HashData(content), info.Sha256);
    }

    [Fact]
    public async Task SweepOnce_DetectionDisabled_NoEmissions_NoRegistryWrites() {
        var fixture = new Fixture();
        var (path, _) = await fixture.WriteBinaryAsync("app.exe", new byte[] { 0xEE });
        await fixture.RegisterAsync(path, sha256: null);
        fixture.AlertSettings.SetSettings(
            enableNewProcessDetection: true,
            enableHashChangeDetection: false,
            enableChainIntegrityMonitor: true);

        await fixture.Monitor.SweepOnceAsync(CancellationToken.None);

        Assert.Empty(fixture.Emitter.Emissions);
        // The registry entry stayed at sha256 = null because the kill-switch
        // short-circuited the sweep before it touched the registry.
        var info = await fixture.Registry.GetByPathAsync(path, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Null(info.Sha256);
    }

    [Fact]
    public async Task SweepOnce_HashChanged_PreservesIdentityColumns_OnRegistryUpdate() {
        // Regression for the Phase 7.5 identity-wipe bug:
        // BinaryHashMonitor.UpdateRegistryAsync used to construct ProcessInfo
        // with only the 6 pre-7.5 args, dropping companyName / productName /
        // installRoot / certSubjectCn / certIssuerCn / signatureStatus to
        // null. SqliteProcessRegistry's INSERT...ON CONFLICT DO UPDATE SET
        // would then NULL the existing row's identity on every hash check,
        // breaking ADR 007's spoof-detection guarantee. Mirrors
        // NewProcessDetector.RefreshLastSeenAsync which preserves all 11
        // fields correctly.
        var fixture = new Fixture();
        var (path, _) = await fixture.WriteBinaryAsync("discord.exe", new byte[] { 0xAB });
        var staleHash = SHA256.HashData(new byte[] { 0x01 });
        await fixture.RegisterFullAsync(
            path,
            sha256: staleHash,
            companyName: "Discord Inc.",
            productName: "Discord",
            installRoot: @"C:\Users\u\AppData\Local\Discord",
            certSubjectCn: "Discord Inc.",
            certIssuerCn: "DigiCert SHA2 Assured ID Code Signing CA",
            signatureStatus: SignatureValidationStatus.Valid);

        await fixture.Monitor.SweepOnceAsync(CancellationToken.None);

        // Hash change emits the alert (sanity).
        var emission = Assert.Single(fixture.Emitter.Emissions);
        Assert.Equal(AlertKind.HashChanged, emission.Kind);

        // Critical assertion: every identity column survives the registry
        // update so the next NewProcess flow can still match logical identity.
        var info = await fixture.Registry.GetByPathAsync(path, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal("Discord Inc.", info.CompanyName);
        Assert.Equal("Discord", info.ProductName);
        Assert.Equal(@"C:\Users\u\AppData\Local\Discord", info.InstallRoot);
        Assert.Equal("Discord Inc.", info.CertSubjectCn);
        Assert.Equal("DigiCert SHA2 Assured ID Code Signing CA", info.CertIssuerCn);
        Assert.Equal(SignatureValidationStatus.Valid, info.SignatureStatus);
    }

    [Fact]
    public async Task SweepOnce_FileMissing_SkipsEntry_NoCrash() {
        var fixture = new Fixture();
        // Register an entry whose file doesn't actually exist on disk.
        var fakePath = Path.Combine(_tempDir, "ghost.exe");
        await fixture.RegisterAsync(fakePath, sha256: null);

        await fixture.Monitor.SweepOnceAsync(CancellationToken.None);

        Assert.Empty(fixture.Emitter.Emissions);
        var info = await fixture.Registry.GetByPathAsync(fakePath, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Null(info.Sha256);  // unchanged — hasher returned null
    }

    [Fact]
    public void Constructor_NullRegistry_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new BinaryHashMonitor(
            processRegistry: null!,
            alertEmitter: new FakeAlertEmitter(),
            options: new FakeOptionsMonitor<AlertOptions>(new AlertOptions()),
            alertSettings: new FakeAlertSettingsState(),
            timeProvider: new FakeTimeProvider(FixedTimestamp),
            logger: NullLogger<BinaryHashMonitor>.Instance));

    private sealed class Fixture {
        private readonly string _tempDir;

        public FakeProcessRegistry Registry { get; } = new();
        public FakeAlertEmitter Emitter { get; } = new();
        public FakeOptionsMonitor<AlertOptions> Options { get; } = new(new AlertOptions());
        public FakeAlertSettingsState AlertSettings { get; } = new();
        public FakeTimeProvider Time { get; } = new(FixedTimestamp);
        public BinaryHashMonitor Monitor { get; }

        public Fixture() {
            _tempDir = Path.Combine(Path.GetTempPath(), "beholder-monitor-tests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempDir);
            Monitor = new BinaryHashMonitor(
                Registry, Emitter, Options, AlertSettings, Time,
                NullLogger<BinaryHashMonitor>.Instance);
        }

        public async Task<(string Path, byte[] Content)> WriteBinaryAsync(string fileName, byte[] content) {
            var path = Path.Combine(_tempDir, fileName);
            await File.WriteAllBytesAsync(path, content);
            return (path, content);
        }

        public Task RegisterAsync(
            string path, byte[]? sha256, DateTimeOffset? lastHashedAt = null
        ) {
            var info = new ProcessInfo(
                path: path,
                displayName: Path.GetFileName(path),
                sha256: sha256,
                firstSeen: FixedTimestamp,
                lastSeen: FixedTimestamp,
                lastHashedAt: lastHashedAt);
            return Registry.RegisterAsync(info, CancellationToken.None);
        }

        /// <summary>
        /// Seed the registry with a fully-identified entry — all six Phase 7.5
        /// identity columns populated. Used by identity-preservation
        /// regression tests.
        /// </summary>
        public Task RegisterFullAsync(
            string path,
            byte[]? sha256,
            string companyName,
            string productName,
            string installRoot,
            string certSubjectCn,
            string certIssuerCn,
            SignatureValidationStatus signatureStatus,
            DateTimeOffset? lastHashedAt = null
        ) {
            var info = new ProcessInfo(
                path: path,
                displayName: Path.GetFileName(path),
                sha256: sha256,
                firstSeen: FixedTimestamp,
                lastSeen: FixedTimestamp,
                lastHashedAt: lastHashedAt,
                companyName: companyName,
                productName: productName,
                installRoot: installRoot,
                certSubjectCn: certSubjectCn,
                certIssuerCn: certIssuerCn,
                signatureStatus: signatureStatus);
            return Registry.RegisterAsync(info, CancellationToken.None);
        }
    }
}
