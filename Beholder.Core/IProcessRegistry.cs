namespace Beholder.Core;

/// <summary>
/// Tracks every binary the daemon has observed on the network, along with its most
/// recent SHA-256 hash. Backed by the <c>process_registry</c> SQLite table.
/// </summary>
public interface IProcessRegistry {
    /// <summary>
    /// Returns the registry entry for the given binary path, or null if the binary
    /// has never been observed.
    /// </summary>
    Task<ProcessInfo?> GetByPathAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a new process record or updates an existing one keyed on
    /// <see cref="ProcessInfo.Path"/>. On update, the implementation refreshes
    /// <see cref="ProcessInfo.LastSeen"/>, <see cref="ProcessInfo.Sha256"/>, and
    /// <see cref="ProcessInfo.LastHashedAt"/> from the supplied record.
    /// </summary>
    Task RegisterAsync(ProcessInfo info, CancellationToken cancellationToken);

    /// <summary>
    /// Returns every registered process. Used by the UI to populate the process list
    /// on snapshot.
    /// </summary>
    Task<IReadOnlyList<ProcessInfo>> ListAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Find a registered process matching the given logical identity, or null
    /// if no match. "Logical identity" = (CompanyName, ProductName, InstallRoot).
    /// All three components must match exactly (case-insensitive on
    /// InstallRoot since paths are case-insensitive on Windows). Used by the
    /// Phase 7.5 NewProcess dedup path to recognize Squirrel-style auto-
    /// updaters (Discord, Slack-old, GitHub Desktop) as a single logical app
    /// across versions. See ADR 007.
    /// </summary>
    Task<ProcessInfo?> FindByLogicalIdentityAsync(
        string companyName, string productName, string installRoot,
        CancellationToken cancellationToken);
}
