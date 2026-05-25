namespace Beholder.Core;

/// <summary>
/// In-memory cache of the most recent chain-verification outcome. Both the
/// periodic <c>ChainIntegrityMonitor</c> hosted service and the
/// user-triggered <c>VerifyChain</c> gRPC method are writers; the Settings
/// tab's Data Storage section (via the <c>GetStorageStats</c> RPC) is the
/// reader.
/// </summary>
/// <remarks>
/// Reads are lock-free via a single volatile reference write per
/// <see cref="Update"/> call. The expected write rate is one per
/// <c>AlertOptions.ChainVerifyIntervalMinutes</c> (default 60 min) plus
/// the occasional user-triggered verify — vanishingly rare contention.
/// Implementations must be thread-safe.
/// </remarks>
public interface IChainStatusCache {
    /// <summary>
    /// Most recent verification snapshot, or null when no verification has
    /// completed in this daemon session.
    /// </summary>
    ChainStatus? Current { get; }

    /// <summary>
    /// Stores a fresh verification outcome. Overwrites any prior value.
    /// </summary>
    void Update(ChainVerificationResult result, DateTimeOffset verifiedAt);
}
