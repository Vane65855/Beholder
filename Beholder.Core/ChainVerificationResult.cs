namespace Beholder.Core;

/// <summary>
/// Outcome of a hash chain verification pass over the event log. Constructed only via
/// the <see cref="Success"/> and <see cref="Failure"/> factories so callers cannot
/// produce inconsistent (valid + failure-details) instances.
/// </summary>
public sealed record ChainVerificationResult {
    /// <summary>True when every row in the chain hashed correctly against its predecessor.</summary>
    public bool IsValid { get; }

    /// <summary>Number of rows the verifier examined before stopping.</summary>
    public long RowsVerified { get; }

    /// <summary>Sequence number of the first broken link, or null when the chain is valid.</summary>
    public long? FailedAtSeq { get; }

    /// <summary>Human-readable failure description, or null when the chain is valid.</summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Sequence number of the signed checkpoint this verification anchored on,
    /// or null when the chain was walked in full from genesis. When set, rows
    /// at or below this seq were trusted via the checkpoint's Ed25519 signature
    /// rather than re-hashed; <see cref="RowsVerified"/> counts only the rows
    /// walked forward from the anchor.
    /// </summary>
    public long? AnchorSeq { get; private init; }

    /// <summary>
    /// Key-id fingerprint of the checkpoint signature that anchored this
    /// verification, or null when full-walked.
    /// </summary>
    public string? AnchorKeyId { get; private init; }

    private ChainVerificationResult(bool isValid, long rowsVerified, long? failedAtSeq, string? errorMessage) {
        IsValid = isValid;
        RowsVerified = rowsVerified;
        FailedAtSeq = failedAtSeq;
        ErrorMessage = errorMessage;
    }

    /// <summary>Builds a successful verification result for the given row count.</summary>
    public static ChainVerificationResult Success(long rowsVerified) {
        ArgumentOutOfRangeException.ThrowIfNegative(rowsVerified);
        return new ChainVerificationResult(isValid: true, rowsVerified, failedAtSeq: null, errorMessage: null);
    }

    /// <summary>Builds a failed verification result describing where and why the chain broke.</summary>
    public static ChainVerificationResult Failure(long rowsVerified, long failedAtSeq, string errorMessage) {
        ArgumentOutOfRangeException.ThrowIfNegative(rowsVerified);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return new ChainVerificationResult(isValid: false, rowsVerified, failedAtSeq, errorMessage);
    }

    /// <summary>
    /// Returns a copy of this result tagged with the checkpoint anchor that
    /// produced it. Used by <see cref="IChainVerifier"/> to annotate a
    /// forward-walk result with the seq + key-id it anchored on.
    /// </summary>
    public ChainVerificationResult WithAnchor(long anchorSeq, string anchorKeyId) {
        ArgumentOutOfRangeException.ThrowIfNegative(anchorSeq);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorKeyId);
        return this with { AnchorSeq = anchorSeq, AnchorKeyId = anchorKeyId };
    }
}
