namespace Beholder.Core;

/// <summary>
/// The daemon's persistent Ed25519 keypair for signing chain checkpoints.
/// One key per install — the implementation generates a fresh keypair on
/// first call if the key files are missing and persists them with restrictive
/// OS permissions; subsequent calls reuse the loaded key. The interface
/// deliberately doesn't expose the private key material; callers can only
/// <see cref="Sign"/> and <see cref="Verify"/>.
/// </summary>
/// <remarks>
/// <para>
/// Loading is lazy: the first access to any member triggers the load-or-generate
/// path. This keeps test-double construction cheap and avoids touching the
/// filesystem during DI graph build.
/// </para>
/// <para>
/// Thread-safety: implementations must be safe to call concurrently. Production
/// loads happen once on the first signer tick; <see cref="Verify"/> may run on
/// any verify-from-anchor path (Phase 11.2).
/// </para>
/// </remarks>
public interface ICheckpointKeyProvider {
    /// <summary>
    /// Stable 16-char lowercase hex fingerprint of the public key
    /// (<c>SHA-256(public_key)[0..8]</c> hex-encoded). Survives daemon restarts
    /// as long as the key file isn't deleted; changes only when the key
    /// rotates.
    /// </summary>
    string KeyId { get; }

    /// <summary>
    /// The 32-byte raw Ed25519 public key.
    /// </summary>
    ReadOnlyMemory<byte> PublicKey { get; }

    /// <summary>
    /// Produces a 64-byte Ed25519 signature over <paramref name="data"/>.
    /// </summary>
    byte[] Sign(ReadOnlySpan<byte> data);

    /// <summary>
    /// True iff <paramref name="signature"/> is a valid Ed25519 signature of
    /// <paramref name="data"/> under the current public key.
    /// </summary>
    bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature);
}
