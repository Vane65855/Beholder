using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Beholder.Core;

/// <summary>
/// Pure-computation primitives for the tamper-evident event log. Builds the canonical
/// byte representation of a row, hashes it with SHA-256, and verifies an existing hash
/// against fresh inputs in constant time. Stateless and dependency-free — there is no
/// I/O here, only math.
/// </summary>
public static class ChainHasher {
    /// <summary>Size of a SHA-256 digest in bytes.</summary>
    public const int HashSize = 32;

    private const int HeaderSize = sizeof(long) + sizeof(long) + sizeof(int);
    private const int StackallocThreshold = 1024;

    /// <summary>
    /// Genesis previous-hash value used when computing the very first row in the chain
    /// (sequence 0). All zero bytes by definition — there is no row before row zero.
    /// </summary>
    public static byte[] ZeroPrevHash { get; } = new byte[HashSize];

    /// <summary>
    /// Computes the canonical SHA-256 hash for a single event-log row. The fields are
    /// concatenated in a fixed big-endian order
    /// (<paramref name="seq"/> ‖ <paramref name="timestampUnixNs"/> ‖
    /// <paramref name="kind"/> ‖ <paramref name="payload"/> ‖
    /// <paramref name="prevHash"/>) and hashed in one shot.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="prevHash"/> is not exactly <see cref="HashSize"/> bytes.
    /// </exception>
    public static byte[] ComputeRowHash(
        long seq,
        long timestampUnixNs,
        EventKind kind,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> prevHash
    ) {
        if (prevHash.Length != HashSize) {
            throw new ArgumentException($"prevHash must be exactly {HashSize} bytes.", nameof(prevHash));
        }

        var totalSize = HeaderSize + payload.Length + HashSize;
        // Typical events fit comfortably in 1 KiB, so the hot path uses a fixed-size
        // stack buffer with no GC pressure. Outsized payloads fall back to the shared
        // ArrayPool to keep large events safe without risking a stack overflow.
        byte[]? rented = null;
        Span<byte> buffer = totalSize <= StackallocThreshold
            ? stackalloc byte[StackallocThreshold]
            : (rented = ArrayPool<byte>.Shared.Rent(totalSize));
        try {
            var workspace = buffer[..totalSize];
            BinaryPrimitives.WriteInt64BigEndian(workspace[..8], seq);
            BinaryPrimitives.WriteInt64BigEndian(workspace.Slice(8, 8), timestampUnixNs);
            BinaryPrimitives.WriteInt32BigEndian(workspace.Slice(16, 4), (int)kind);
            payload.CopyTo(workspace.Slice(HeaderSize, payload.Length));
            prevHash.CopyTo(workspace.Slice(HeaderSize + payload.Length, HashSize));
            return SHA256.HashData(workspace);
        } finally {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Recomputes the row hash for the given inputs and compares it to
    /// <paramref name="expectedHash"/> in constant time. Use this when verifying a
    /// stored chain row — never use a non-cryptographic equality check.
    /// </summary>
    /// <returns>True when the recomputed hash matches; false otherwise.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="expectedHash"/> or <paramref name="prevHash"/> is
    /// not exactly <see cref="HashSize"/> bytes.
    /// </exception>
    public static bool Verify(
        long seq,
        long timestampUnixNs,
        EventKind kind,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> prevHash,
        ReadOnlySpan<byte> expectedHash
    ) {
        if (expectedHash.Length != HashSize) {
            throw new ArgumentException($"expectedHash must be exactly {HashSize} bytes.", nameof(expectedHash));
        }
        var actualHash = ComputeRowHash(seq, timestampUnixNs, kind, payload, prevHash);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
