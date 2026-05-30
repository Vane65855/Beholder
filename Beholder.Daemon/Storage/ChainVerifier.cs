using Beholder.Core;

namespace Beholder.Daemon.Storage;

/// <summary>
/// Verifies the chain-hashed event log, anchoring on the latest signed
/// checkpoint when one is usable so the periodic re-verify and the
/// user-triggered <c>VerifyChain</c> RPC skip re-walking rows the daemon has
/// already attested. Composes <see cref="IEventStore"/>,
/// <see cref="ICheckpointStore"/>, and <see cref="ICheckpointKeyProvider"/>.
/// </summary>
/// <remarks>
/// The anchor is both an optimization and a tamper-evidence mechanism. A
/// fully cascaded rewrite of the event log produces an internally-consistent
/// chain that a plain hash walk accepts — only a signed checkpoint catches it,
/// because the rewritten head can't match a signature the attacker can't
/// forge. That is why a signature-valid checkpoint whose signed head no longer
/// matches the live chain is reported as a <em>failure</em>, not silently
/// retried as a full walk (see case 5 in <see cref="VerifyAsync"/>).
/// </remarks>
internal sealed class ChainVerifier : IChainVerifier {
    private readonly IEventStore _eventStore;
    private readonly ICheckpointStore _checkpointStore;
    private readonly ICheckpointKeyProvider _keyProvider;
    private readonly ILogger<ChainVerifier> _logger;

    public ChainVerifier(
        IEventStore eventStore,
        ICheckpointStore checkpointStore,
        ICheckpointKeyProvider keyProvider,
        ILogger<ChainVerifier> logger
    ) {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(keyProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _eventStore = eventStore;
        _checkpointStore = checkpointStore;
        _keyProvider = keyProvider;
        _logger = logger;
    }

    public async Task<ChainVerificationResult> VerifyAsync(bool forceFull, CancellationToken cancellationToken) {
        // Case 1: explicit full walk (mandatory startup verify, paranoid audit).
        if (forceFull) {
            return await _eventStore.VerifyAsync(cancellationToken).ConfigureAwait(false);
        }

        // Case 2: no checkpoint to anchor on (young chain, or signing disabled).
        var checkpoint = await _checkpointStore.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        if (checkpoint is null) {
            return await _eventStore.VerifyAsync(cancellationToken).ConfigureAwait(false);
        }

        // Case 3: the checkpoint's signature doesn't verify under the current
        // key. Benign cause: the key was rotated (key files deleted +
        // regenerated), so old checkpoints are signed by a key we no longer
        // hold. Operational event, not tampering — fall back to a full walk.
        var signedPayload = CheckpointSignaturePayload.Build(
            checkpoint.Seq,
            checkpoint.RowHash,
            checkpoint.Timestamp.ToUnixTimeMilliseconds() * 1_000_000L);
        if (!_keyProvider.Verify(signedPayload, checkpoint.Signature)) {
            _logger.LogInformation(
                "Latest checkpoint at seq {Seq} has an unverifiable signature " +
                "(key rotated or corrupt); falling back to full chain walk", checkpoint.Seq);
            return await _eventStore.VerifyAsync(cancellationToken).ConfigureAwait(false);
        }

        // Case 4: the checkpoint references a seq the live chain no longer has
        // (truncation). Suspicious, but the full walk is authoritative.
        var liveRowHash = await _eventStore
            .TryGetRowHashAsync(checkpoint.Seq, cancellationToken)
            .ConfigureAwait(false);
        if (liveRowHash is null) {
            _logger.LogWarning(
                "Latest checkpoint references seq {Seq} not present in the chain " +
                "(truncation?); falling back to full chain walk", checkpoint.Seq);
            return await _eventStore.VerifyAsync(cancellationToken).ConfigureAwait(false);
        }

        // Case 5: signature is authentic, but the live row at the signed seq no
        // longer hashes to what was signed. The chain head was altered after it
        // was attested — tamper evidence. Do NOT fall back: a cascaded rewrite
        // would pass a full walk, so reporting failure here is the whole point
        // of the signature.
        if (!liveRowHash.AsSpan().SequenceEqual(checkpoint.RowHash)) {
            return ChainVerificationResult.Failure(
                rowsVerified: 0,
                failedAtSeq: checkpoint.Seq,
                errorMessage: $"checkpoint row_hash mismatch at seq {checkpoint.Seq}: " +
                    "chain head was altered after it was signed");
        }

        // Case 6: anchor confirmed. Walk forward from the row after the anchor,
        // expecting its prev_hash to chain off the signed row_hash.
        var result = await _eventStore
            .VerifyFromAsync(checkpoint.Seq + 1, checkpoint.RowHash, cancellationToken)
            .ConfigureAwait(false);
        return result.WithAnchor(checkpoint.Seq, checkpoint.KeyId);
    }
}
