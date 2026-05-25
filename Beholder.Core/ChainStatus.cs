namespace Beholder.Core;

/// <summary>
/// Most recent chain-verification outcome captured by either the periodic
/// <c>ChainIntegrityMonitor</c> or the user-triggered <c>VerifyChain</c> RPC,
/// plus the wall-clock time at which it was captured. Surfaced to the UI by
/// <see cref="IChainStatusCache"/> so the Settings tab's Maintenance section
/// can render "last verified: 3 minutes ago".
/// </summary>
/// <remarks>
/// Wrapping <see cref="ChainVerificationResult"/> (rather than duplicating its
/// fields) preserves the factory-validated invariants of the result type:
/// callers cannot construct an inconsistent (valid + failure-details) snapshot.
/// </remarks>
public sealed record ChainStatus(
    DateTimeOffset LastVerifiedAt,
    ChainVerificationResult Result
);
