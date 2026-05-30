using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Pipeline;
using Beholder.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Beholder.Tests;

public sealed class ChainIntegrityMonitorTests {
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 4, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VerifyOnce_ChainValid_NoAlert() {
        var fixture = new Fixture();
        fixture.ChainVerifier.Result = ChainVerificationResult.Success(rowsVerified: 42);

        await fixture.Monitor.VerifyOnceAsync(forceFull: false, CancellationToken.None);

        Assert.Empty(fixture.Emitter.Emissions);
    }

    [Fact]
    public async Task VerifyOnce_ChainInvalid_EmitsAlertWithFailedSeq() {
        var fixture = new Fixture();
        fixture.ChainVerifier.Result = ChainVerificationResult.Failure(
            rowsVerified: 5, failedAtSeq: 6, errorMessage: "row_hash mismatch at seq 6");

        await fixture.Monitor.VerifyOnceAsync(forceFull: false, CancellationToken.None);

        var emission = Assert.Single(fixture.Emitter.Emissions);
        Assert.Equal(AlertKind.ChainError, emission.Kind);
        Assert.Equal(string.Empty, emission.ProcessPath);  // ChainError has no associated process
        Assert.Contains("seq 6", emission.Summary);
        Assert.Contains("row_hash mismatch", emission.Summary);
    }

    [Fact]
    public async Task VerifyOnce_VerifyThrows_LogsAndDoesNotEmit() {
        // VerifyAsync throwing is treated as transient infrastructure
        // failure (DB lock, IO blip), not chain corruption — no alert.
        var fixture = new Fixture();
        fixture.ChainVerifier.Exception = new InvalidOperationException("DB locked");

        await fixture.Monitor.VerifyOnceAsync(forceFull: false, CancellationToken.None);

        Assert.Empty(fixture.Emitter.Emissions);
    }

    [Fact]
    public async Task VerifyOnce_Success_UpdatesChainStatusCache() {
        // Phase 13.1: every successful verification snapshots the result +
        // the wall-clock time into IChainStatusCache so the Settings tab's
        // Maintenance section can render "last verified: 3m ago — valid".
        var fixture = new Fixture();
        fixture.ChainVerifier.Result = ChainVerificationResult.Success(rowsVerified: 99);

        await fixture.Monitor.VerifyOnceAsync(forceFull: false, CancellationToken.None);

        var update = Assert.Single(fixture.ChainStatusCache.UpdateCalls);
        Assert.True(update.Result.IsValid);
        Assert.Equal(99, update.Result.RowsVerified);
        Assert.Equal(FixedTimestamp, update.VerifiedAt);
    }

    [Fact]
    public async Task VerifyOnce_Failure_AlsoUpdatesChainStatusCache() {
        // Phase 13.1: verification *failures* must also reach the cache so
        // the Settings tab surfaces the most-recent outcome regardless of
        // whether the chain was valid. (Distinct from VerifyAsync throwing,
        // which is transient infra and is correctly skipped.)
        var fixture = new Fixture();
        fixture.ChainVerifier.Result = ChainVerificationResult.Failure(
            rowsVerified: 7, failedAtSeq: 8, errorMessage: "hash mismatch");

        await fixture.Monitor.VerifyOnceAsync(forceFull: false, CancellationToken.None);

        var update = Assert.Single(fixture.ChainStatusCache.UpdateCalls);
        Assert.False(update.Result.IsValid);
        Assert.Equal(8, update.Result.FailedAtSeq);
    }

    [Fact]
    public async Task VerifyOnce_VerifyThrows_DoesNotUpdateChainStatusCache() {
        // Transient VerifyAsync failures must not overwrite the last real
        // verification result — otherwise a 5-minute DB lock would
        // discard a perfectly good "verified 3 minutes ago" snapshot.
        var fixture = new Fixture();
        fixture.ChainVerifier.Exception = new InvalidOperationException("DB locked");

        await fixture.Monitor.VerifyOnceAsync(forceFull: false, CancellationToken.None);

        Assert.Empty(fixture.ChainStatusCache.UpdateCalls);
    }

    [Fact]
    public async Task VerifyOnce_PeriodicTick_UsesAnchoredVerify() {
        // Phase 11.2: the periodic re-verify uses the fast checkpoint anchor
        // (forceFull: false). The mandatory startup verify is the only
        // forceFull: true caller (covered by the StartAsync test below).
        var fixture = new Fixture();
        fixture.ChainVerifier.Result = ChainVerificationResult.Success(rowsVerified: 1);

        await fixture.Monitor.VerifyOnceAsync(forceFull: false, CancellationToken.None);

        Assert.Equal(false, fixture.ChainVerifier.LastForceFull);
    }

    [Fact]
    public async Task StartAsync_DetectionDisabled_StillRunsMandatoryStartupVerify() {
        // Phase 13.3 hardening: the mandatory startup chain verify can no
        // longer be skipped via the EnableChainIntegrityMonitor toggle.
        // Rationale: chain corruption is most likely to be discovered at
        // startup (power loss mid-write, manual SQL tampering between
        // daemon runs); allowing the UI toggle to silence the startup
        // check would silently hide corruption — exactly what the chain
        // exists to prevent. The toggle now gates only the periodic loop.
        // Phase 11.2: the startup verify is forceFull: true (full walk).
        var fixture = new Fixture();
        fixture.AlertSettings.SetSettings(
            enableNewProcessDetection: true,
            enableHashChangeDetection: true,
            enableChainIntegrityMonitor: false);
        fixture.ChainVerifier.Result = ChainVerificationResult.Failure(
            rowsVerified: 0, failedAtSeq: 1, errorMessage: "boom");

        var ct = TestContext.Current.CancellationToken;
        await fixture.Monitor.StartAsync(ct);
        // Grace window for the mandatory startup verify to fire.
        await Task.Delay(50, ct);
        await fixture.Monitor.StopAsync(ct);

        // The startup verify emitted the failure alert, and used the full walk.
        Assert.Single(fixture.Emitter.Emissions);
        Assert.Equal(AlertKind.ChainError, fixture.Emitter.Emissions[0].Kind);
        Assert.Equal(true, fixture.ChainVerifier.LastForceFull);
    }

    [Fact]
    public void Constructor_NullChainVerifier_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new ChainIntegrityMonitor(
            chainVerifier: null!,
            alertEmitter: new FakeAlertEmitter(),
            chainStatusCache: new FakeChainStatusCache(),
            options: new FakeOptionsMonitor<AlertOptions>(new AlertOptions()),
            alertSettings: new FakeAlertSettingsState(),
            timeProvider: new FakeTimeProvider(FixedTimestamp),
            logger: NullLogger<ChainIntegrityMonitor>.Instance));

    private sealed class Fixture {
        public FakeChainVerifier ChainVerifier { get; } = new();
        public FakeAlertEmitter Emitter { get; } = new();
        public FakeChainStatusCache ChainStatusCache { get; } = new();
        public FakeOptionsMonitor<AlertOptions> Options { get; } = new(new AlertOptions());
        public FakeAlertSettingsState AlertSettings { get; } = new();
        public FakeTimeProvider Time { get; } = new(FixedTimestamp);
        public ChainIntegrityMonitor Monitor { get; }

        public Fixture() {
            Monitor = new ChainIntegrityMonitor(
                ChainVerifier, Emitter, ChainStatusCache, Options, AlertSettings, Time,
                NullLogger<ChainIntegrityMonitor>.Instance);
        }
    }
}
