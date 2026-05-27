namespace Beholder.Core;

/// <summary>
/// Persistence layer for manual application-identity rules (Phase 13.6). The
/// daemon consults this store as Tier 2.5 in
/// <c>NewProcessDetector.ProcessAsync</c> — after the automatic logical-
/// identity check (ADR 007) but before falling back to fire a fresh
/// <see cref="AlertKind.NewProcess"/> alert. A rule match means "this binary
/// is the same logical app as the prior versions under the same anchor —
/// register the path silently."
/// </summary>
/// <remarks>
/// Rules are uniquely keyed on (<see cref="AppIdentityRule.AnchorPath"/>,
/// <see cref="AppIdentityRule.Filename"/>). Duplicate adds return null (the
/// RPC handler surfaces that as a soft-failure). Removes are idempotent.
/// </remarks>
public interface IAppIdentityRuleStore {
    /// <summary>
    /// Inserts a new rule with the given anchor + filename + optional display
    /// name. Returns the materialized row (including the database-assigned
    /// ID + created-at timestamp). Returns <c>null</c> if a rule with the
    /// same (anchor, filename) already exists — the caller treats this as a
    /// soft-failure (no exception, no chain write).
    /// </summary>
    Task<AppIdentityRule?> AddAsync(
        string anchorPath, string filename, string? displayName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes the rule with the given ID. Returns <c>true</c> if a row was
    /// deleted, <c>false</c> if no rule had that ID (idempotent).
    /// </summary>
    Task<bool> RemoveAsync(int id, CancellationToken cancellationToken);

    /// <summary>Returns all persisted rules in ID order (insertion order).</summary>
    Task<IReadOnlyList<AppIdentityRule>> ListAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the rule that matches the binary at <paramref name="fullPath"/>
    /// per the strict depth-1 semantics on <see cref="AppIdentityRule"/>, or
    /// <c>null</c> if no rule matches. <paramref name="filename"/> must equal
    /// <c>Path.GetFileName(fullPath)</c> — the caller pre-computes it so the
    /// store can use its filename index for the lookup.
    /// </summary>
    Task<AppIdentityRule?> MatchAsync(
        string filename, string fullPath, CancellationToken cancellationToken);
}
