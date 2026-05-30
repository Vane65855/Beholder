namespace Beholder.Core;

/// <summary>
/// Verifies the chain-hashed event log, optionally anchoring on the latest
/// signed checkpoint to skip re-walking rows the daemon has already attested.
/// Composes <see cref="IEventStore"/> (the chain walk), <see cref="ICheckpointStore"/>
/// (the latest anchor), and <see cref="ICheckpointKeyProvider"/> (signature
/// verification). The anchor is a pure optimization for the periodic re-verify
/// and the user-triggered <c>VerifyChain</c> RPC; the mandatory startup verify
/// passes <paramref name="forceFull"/> = true to walk from genesis.
/// </summary>
public interface IChainVerifier {
    /// <summary>
    /// Verifies the chain. When <paramref name="forceFull"/> is false and a
    /// usable signed checkpoint exists, verification anchors at that checkpoint
    /// and walks forward only; otherwise it walks the entire chain from the
    /// first row. A checkpoint whose signed head no longer matches the live
    /// chain is reported as a failure (tamper evidence) rather than silently
    /// falling back. Side-effect-free and idempotent.
    /// </summary>
    Task<ChainVerificationResult> VerifyAsync(bool forceFull, CancellationToken cancellationToken);
}
