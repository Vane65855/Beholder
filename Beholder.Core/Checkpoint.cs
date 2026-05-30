namespace Beholder.Core;

/// <summary>
/// One signed attestation of the chain-hashed event log's head at a moment in
/// time. The daemon's <c>CheckpointSignerService</c> writes these periodically;
/// <see cref="IEventStore.VerifyAsync"/> (Phase 11.2) uses the latest as an
/// anchor so verification can skip rows already implicitly attested by the
/// signature instead of walking from <c>seq=1</c>.
/// </summary>
/// <param name="Seq">The <c>event_log.seq</c> this checkpoint endorses.</param>
/// <param name="RowHash">
/// The 32-byte <c>row_hash</c> stored at <paramref name="Seq"/> at the moment
/// the signature was produced. Verification recomputes this against the live
/// <c>event_log</c> row to detect post-checkpoint tampering of the anchor row.
/// </param>
/// <param name="Timestamp">When the signature was produced.</param>
/// <param name="Signature">
/// 64-byte Ed25519 signature over <c>seq ‖ row_hash ‖ ts_unix_ns</c> (big-endian),
/// matching the schema comment in <c>DatabaseInitializer.CreateTables</c>.
/// </param>
/// <param name="KeyId">
/// Stable fingerprint of the public key that produced <paramref name="Signature"/>.
/// 16 lowercase hex chars derived from <c>SHA-256(public_key)</c>. Lets a future
/// multi-key rotation surface recognize historical signatures; v1 only emits
/// checkpoints with the current key's id.
/// </param>
/// <remarks>
/// Record equality compares the byte-array references, not their contents — do
/// not use <see cref="object.Equals(object?)"/> for chain-state comparisons.
/// Use <c>RowHash.AsSpan().SequenceEqual(other.RowHash)</c> instead.
/// </remarks>
public sealed record Checkpoint(
    long Seq,
    byte[] RowHash,
    DateTimeOffset Timestamp,
    byte[] Signature,
    string KeyId);
