using System.Buffers.Binary;

namespace Beholder.Core;

/// <summary>
/// The canonical byte representation a chain checkpoint's Ed25519 signature
/// covers: <c>seq(8) ‖ row_hash(32) ‖ ts_unix_ns(8)</c>, big-endian. Defined
/// once here so the signer (<c>CheckpointSignerService</c>) and the verifier
/// (<c>ChainVerifier</c>) produce byte-identical input by construction —
/// mirroring the "canonical representation defined once" rule that
/// <see cref="ChainHasher"/> follows for row hashes.
/// </summary>
public static class CheckpointSignaturePayload {
    /// <summary>Total signed-payload size: 8-byte seq + 32-byte row hash + 8-byte timestamp.</summary>
    public const int Size = sizeof(long) + ChainHasher.HashSize + sizeof(long);

    /// <summary>
    /// Serialises a checkpoint's signed fields into the canonical big-endian
    /// layout. The same <paramref name="timestampUnixNs"/> that is stored in
    /// the checkpoint's <c>ts_unix_ns</c> column must be passed here — the
    /// signer and verifier both reconstruct the payload from the stored
    /// timestamp, so signing over any other value (e.g. the head row's own
    /// timestamp) would make verification fail.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="rowHash"/> is not exactly
    /// <see cref="ChainHasher.HashSize"/> bytes.
    /// </exception>
    public static byte[] Build(long seq, ReadOnlySpan<byte> rowHash, long timestampUnixNs) {
        if (rowHash.Length != ChainHasher.HashSize) {
            throw new ArgumentException(
                $"rowHash must be exactly {ChainHasher.HashSize} bytes.", nameof(rowHash));
        }
        var buffer = new byte[Size];
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(0, sizeof(long)), seq);
        rowHash.CopyTo(buffer.AsSpan(sizeof(long), ChainHasher.HashSize));
        BinaryPrimitives.WriteInt64BigEndian(
            buffer.AsSpan(sizeof(long) + ChainHasher.HashSize, sizeof(long)), timestampUnixNs);
        return buffer;
    }
}
