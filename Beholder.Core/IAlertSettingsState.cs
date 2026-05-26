namespace Beholder.Core;

/// <summary>
/// Runtime-mutable state for the Alerts section of Settings — the daemon's
/// three master kill-switches for the alert detector pipeline. Same shape as
/// <see cref="IRecordingSettingsState"/> and <see cref="IHostnameResolutionSettingsState"/>:
/// read-only getters, one atomic <c>SetSettings</c> for the whole section,
/// and a <see cref="StateChanged"/> event.
/// </summary>
/// <remarks>
/// <para>All three toggles take effect immediately. <c>NewProcessDetector</c>
/// reads <see cref="EnableNewProcessDetection"/> per flow event (hot path);
/// <c>BinaryHashMonitor</c> and <c>ChainIntegrityMonitor</c> read their
/// respective bools per periodic tick. None of the three numeric thresholds
/// in <c>AlertOptions</c> (interval cadences, file-hash timeout) are exposed
/// here — those remain JSON-only advanced tuning per the Phase 13.2 precedent
/// for queue capacities and buffer sizes.</para>
/// <para>Initial values seeded from <c>IOptions&lt;AlertOptions&gt;</c> at
/// construction; persisted overrides applied by the
/// <c>SettingsOverridesService</c> hosted service at startup.</para>
/// </remarks>
public interface IAlertSettingsState {
    /// <summary>
    /// When true, <c>NewProcessDetector</c> emits <c>NewProcess</c> alerts the
    /// first time each logical app touches the network. Read per flow event on
    /// the hot path.
    /// </summary>
    bool EnableNewProcessDetection { get; }

    /// <summary>
    /// When true, the periodic <c>BinaryHashMonitor</c> tick emits
    /// <c>HashChanged</c> alerts when a tracked binary's SHA-256 differs from
    /// the previously stored value (covers both literal hash changes and the
    /// Phase 7.5 logical-identity-publisher-mismatch spoof case).
    /// </summary>
    bool EnableHashChangeDetection { get; }

    /// <summary>
    /// When true, the periodic <c>ChainIntegrityMonitor</c> tick runs
    /// <see cref="IEventStore.VerifyAsync"/> and emits <c>ChainError</c>
    /// alerts on failure. The mandatory startup chain verification always
    /// runs regardless of this toggle (Phase 13.3 hardening — a corrupt
    /// chain shouldn't be silenceable via a UI flip).
    /// </summary>
    bool EnableChainIntegrityMonitor { get; }

    /// <summary>
    /// Atomically updates the Alerts section's settings. Returns true if any
    /// of the three values changed; false on a no-op set. Fires
    /// <see cref="StateChanged"/> only on real transitions.
    /// </summary>
    bool SetSettings(
        bool enableNewProcessDetection,
        bool enableHashChangeDetection,
        bool enableChainIntegrityMonitor);

    event Action<AlertSettingsSnapshot>? StateChanged;
}

/// <summary>
/// Immutable snapshot of the Alerts section's state, passed to
/// <see cref="IAlertSettingsState.StateChanged"/> subscribers.
/// </summary>
public sealed record AlertSettingsSnapshot(
    bool EnableNewProcessDetection,
    bool EnableHashChangeDetection,
    bool EnableChainIntegrityMonitor);
