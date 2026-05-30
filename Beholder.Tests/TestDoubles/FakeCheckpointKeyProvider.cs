using System.Security.Cryptography;
using Beholder.Core;
using NSec.Cryptography;
using NsecKey = NSec.Cryptography.Key;
using NsecPublicKey = NSec.Cryptography.PublicKey;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="ICheckpointKeyProvider"/> backed by a real NSec
/// Ed25519 keypair generated in the constructor. Tests use this so signature
/// round-trips exercise real Ed25519 (catching genuine signature bugs) without
/// touching the filesystem.
/// </summary>
internal sealed class FakeCheckpointKeyProvider : ICheckpointKeyProvider, IDisposable {
    private readonly NsecKey _key;
    private readonly byte[] _publicKey;
    private readonly string _keyId;
    private bool _disposed;

    public FakeCheckpointKeyProvider() {
        var parameters = new KeyCreationParameters {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        };
        _key = NsecKey.Create(SignatureAlgorithm.Ed25519, parameters);
        _publicKey = _key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var hash = SHA256.HashData(_publicKey);
        _keyId = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public string KeyId => _keyId;
    public ReadOnlyMemory<byte> PublicKey => _publicKey;

    public byte[] Sign(ReadOnlySpan<byte> data) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SignatureAlgorithm.Ed25519.Sign(_key, data);
    }

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var publicKey = NsecPublicKey.Import(
            SignatureAlgorithm.Ed25519, _publicKey, KeyBlobFormat.RawPublicKey);
        return SignatureAlgorithm.Ed25519.Verify(publicKey, data, signature);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _key.Dispose();
    }
}
