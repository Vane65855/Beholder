namespace Beholder.Core;

/// <summary>
/// Runtime-mutable state for the Recording section of Settings — the daemon's
/// "what do we capture?" knobs. Today: one toggle (<see cref="FilterSelfTraffic"/>).
/// Mirrors <c>IFirewallEnforcementState</c>'s shape (Phase 6.4 precedent): a
/// read-only getter, a Set method that returns whether the value actually
/// changed, and a <see cref="StateChanged"/> event for consumers that need
/// to react to transitions.
/// </summary>
/// <remarks>
/// <para>Initial state seeded from <c>RecordingOptions</c> at construction. The
/// <c>SettingsOverridesService</c> hosted service applies persisted overrides
/// at startup before any consumer reads the state.</para>
/// <para>The <c>SetSettings</c> method is atomic across the whole section's
/// bundle — for one-toggle Recording today this is trivial, but the pattern
/// generalises to sections with multiple correlated knobs.</para>
/// </remarks>
public interface IRecordingSettingsState {
    /// <summary>
    /// When true, the flow pipeline drops events whose process is Beholder
    /// itself (the daemon and UI). Read on the hot path by
    /// <c>FlowEventPipeline.OnFlowEventReceived</c> — implementations must
    /// expose this as a non-allocating volatile read.
    /// </summary>
    bool FilterSelfTraffic { get; }

    /// <summary>
    /// Atomically updates the Recording section's settings. Returns true if
    /// any value actually changed; false when the new bundle matches the
    /// current state (idempotent — re-asserting current values is a no-op).
    /// Fires <see cref="StateChanged"/> only on real transitions.
    /// </summary>
    bool SetSettings(bool filterSelfTraffic);

    /// <summary>
    /// Fired after a real state transition with the new snapshot. Subscribers
    /// must not block — the event fires synchronously from inside
    /// <see cref="SetSettings"/>'s caller (typically a gRPC handler).
    /// </summary>
    event Action<RecordingSettingsSnapshot>? StateChanged;
}

/// <summary>
/// Immutable snapshot of the Recording section's state, passed to
/// <see cref="IRecordingSettingsState.StateChanged"/> subscribers.
/// </summary>
public sealed record RecordingSettingsSnapshot(bool FilterSelfTraffic);
