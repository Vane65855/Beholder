namespace Beholder.Core;

/// <summary>
/// Runtime-mutable state for the Traffic Totals section of Settings — the
/// user-curated list of process paths excluded from aggregate traffic views
/// ("Exclude from totals"). Same shape as <see cref="IAlertSettingsState"/>:
/// read-only getters, one atomic set for the whole section, and a
/// <see cref="StateChanged"/> event.
/// </summary>
/// <remarks>
/// <para>Exclusion is an aggregate-<em>read</em>-time concern, deliberately NOT
/// the capture-time drop that <c>Recording.FilterSelfTraffic</c> performs.
/// Excluded processes stay fully captured, recorded to every rollup tier,
/// alertable, and chain-audited; they are only removed from all-processes
/// aggregates (totals, aggregate timeline, breakdowns) and flagged on
/// per-process snapshots so the UI can hide or mark their rows. Views scoped
/// to a specific process always include it — explicit selection wins.</para>
/// <para>Matching is by process path, case-insensitive: exclusion entries come
/// from a file picker while recorded paths come from ETW, and Windows paths
/// are case-insensitive.</para>
/// <para>Seeds empty at construction (a user-curated list has no meaningful
/// <c>appsettings.json</c> default); the persisted list is applied by the
/// <c>SettingsOverridesService</c> hosted service at startup.</para>
/// </remarks>
public interface ITotalsExclusionState {
    /// <summary>
    /// The current excluded process paths, in the user's list order. Returns
    /// an immutable snapshot — safe to enumerate without holding a lock.
    /// </summary>
    IReadOnlyList<string> ExcludedProcessPaths { get; }

    /// <summary>
    /// True when <paramref name="processPath"/> is on the exclusion list
    /// (ordinal case-insensitive). Read once per snapshot batch / aggregate
    /// query, so it must stay cheap.
    /// </summary>
    bool IsExcluded(string processPath);

    /// <summary>
    /// Atomically replaces the exclusion list. Returns true if the effective
    /// set changed (order-insensitive, case-insensitive comparison); false on
    /// a no-op set. Fires <see cref="StateChanged"/> only on real transitions.
    /// Callers pass an already-normalized list (trimmed, non-empty, deduped).
    /// </summary>
    bool SetExcludedPaths(IReadOnlyList<string> excludedProcessPaths);

    event Action<TotalsExclusionSnapshot>? StateChanged;
}

/// <summary>
/// Immutable snapshot of the Traffic Totals section's state, passed to
/// <see cref="ITotalsExclusionState.StateChanged"/> subscribers.
/// </summary>
public sealed record TotalsExclusionSnapshot(IReadOnlyList<string> ExcludedProcessPaths);
