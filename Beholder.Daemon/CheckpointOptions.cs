namespace Beholder.Daemon;

/// <summary>
/// Configuration for the Phase 11 chain-checkpoint signer. Mirrors the
/// <c>AlertOptions</c> shape — defaults are sensible for a fresh install;
/// values bind from the <c>"Checkpoint"</c> section of <c>appsettings.json</c>.
/// </summary>
public sealed class CheckpointOptions {
    /// <summary>
    /// How often <c>CheckpointSignerService</c> wakes up to attempt signing the
    /// current chain head. Matches <c>AlertOptions.ChainVerifyIntervalMinutes</c>'s
    /// default so the chain gets re-attested on roughly the same cadence as it
    /// gets re-verified.
    /// </summary>
    public TimeSpan SigningInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Master kill-switch. When false, the signer service starts but every
    /// periodic tick is a no-op — useful for tests and for users who want
    /// the verifier behavior but not the signer's write traffic. The
    /// mandatory startup chain verify (<c>ChainIntegrityMonitor</c>) is
    /// independent and not affected.
    /// </summary>
    public bool EnableSigning { get; init; } = true;
}
