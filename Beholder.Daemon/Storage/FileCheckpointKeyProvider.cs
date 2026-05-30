using System.Security.Cryptography;
using Beholder.Core;
using NSec.Cryptography;
using NsecKey = NSec.Cryptography.Key;
using NsecPublicKey = NSec.Cryptography.PublicKey;

namespace Beholder.Daemon.Storage;

/// <summary>
/// File-backed implementation of <see cref="ICheckpointKeyProvider"/>. On
/// first use, lazily generates a fresh Ed25519 keypair and writes it to the
/// configured folder with restrictive OS permissions. Subsequent calls reuse
/// the loaded key. The private key never leaves this object — callers can
/// only <see cref="Sign"/> and <see cref="Verify"/>.
/// </summary>
/// <remarks>
/// <para>
/// Three files live in <c>KeyFolder</c>:
/// <list type="bullet">
///   <item><c>checkpoint-private.bin</c> — 32-byte raw Ed25519 seed.</item>
///   <item><c>checkpoint-public.bin</c> — 32-byte raw public key.</item>
///   <item><c>checkpoint-key-id.txt</c> — 16-char lowercase hex fingerprint
///     of the public key (first 8 bytes of <c>SHA-256(public_key)</c>).</item>
/// </list>
/// </para>
/// <para>
/// Linux: each file is created with <c>UserRead | UserWrite</c> mode
/// (<c>0600</c>) via <see cref="File.SetUnixFileMode(string, UnixFileMode)"/>.
/// Windows: the daemon's data folder is already in a privileged location
/// (<c>ProgramData\Beholder</c> when installed as a service), and the data
/// folder's ACLs inherited from <c>ProgramData</c> restrict to SYSTEM +
/// Administrators by default. No DPAPI in v1; see ADR 012's "Key storage"
/// section for the rationale.
/// </para>
/// <para>
/// Key generation strategy: if any of the three files is missing or malformed,
/// the entire keypair regenerates atomically (writes to <c>&lt;file&gt;.tmp</c>
/// + <see cref="File.Move(string, string, bool)"/>). Existing checkpoints
/// signed by a now-overwritten key fail signature verification on the next
/// anchored verify and that path falls back to a full walk — correct behavior
/// for a key-loss scenario.
/// </para>
/// </remarks>
internal sealed class FileCheckpointKeyProvider : ICheckpointKeyProvider, IDisposable {
    private const string PrivateKeyFileName = "checkpoint-private.bin";
    private const string PublicKeyFileName = "checkpoint-public.bin";
    private const string KeyIdFileName = "checkpoint-key-id.txt";
    private const int Ed25519SeedSize = 32;
    private const int Ed25519PublicKeySize = 32;
    private const int KeyIdHexLength = 16;

    private readonly string _keyFolder;
    private readonly ILogger<FileCheckpointKeyProvider> _logger;
    private readonly object _gate = new();
    private NsecKey? _key;
    private byte[]? _publicKey;
    private string? _keyId;
    private bool _disposed;

    public FileCheckpointKeyProvider(string keyFolder, ILogger<FileCheckpointKeyProvider> logger) {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFolder);
        ArgumentNullException.ThrowIfNull(logger);
        _keyFolder = keyFolder;
        _logger = logger;
    }

    public string KeyId {
        get { EnsureLoaded(); return _keyId!; }
    }

    public ReadOnlyMemory<byte> PublicKey {
        get { EnsureLoaded(); return _publicKey!; }
    }

    public byte[] Sign(ReadOnlySpan<byte> data) {
        EnsureLoaded();
        return SignatureAlgorithm.Ed25519.Sign(_key!, data);
    }

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) {
        EnsureLoaded();
        var publicKey = NsecPublicKey.Import(
            SignatureAlgorithm.Ed25519, _publicKey!, KeyBlobFormat.RawPublicKey);
        return SignatureAlgorithm.Ed25519.Verify(publicKey, data, signature);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) {
            _key?.Dispose();
            _key = null;
        }
    }

    /// <summary>
    /// Lazy load-or-generate. Always takes the lock — double-checked locking
    /// on reference fields requires <see cref="System.Threading.Volatile"/>
    /// reads to be correct post-.NET 5, and the contention here is negligible
    /// (production call rate is one signer tick per hour). Simpler is better.
    /// </summary>
    private void EnsureLoaded() {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_key is not null) return;
            LoadOrCreate();
        }
    }

    private void LoadOrCreate() {
        Directory.CreateDirectory(_keyFolder);
        var privatePath = Path.Combine(_keyFolder, PrivateKeyFileName);
        var publicPath = Path.Combine(_keyFolder, PublicKeyFileName);
        var keyIdPath = Path.Combine(_keyFolder, KeyIdFileName);

        if (TryLoadExisting(privatePath, publicPath, keyIdPath)) {
            _logger.LogInformation(
                "Loaded existing checkpoint signing key from {Folder} (keyId={KeyId})",
                _keyFolder, _keyId);
            return;
        }

        Generate(privatePath, publicPath, keyIdPath);
        _logger.LogInformation(
            "Generated new checkpoint signing key in {Folder} (keyId={KeyId})",
            _keyFolder, _keyId);
    }

    private bool TryLoadExisting(string privatePath, string publicPath, string keyIdPath) {
        if (!File.Exists(privatePath) || !File.Exists(publicPath) || !File.Exists(keyIdPath)) {
            return false;
        }
        try {
            var privateSeed = File.ReadAllBytes(privatePath);
            var publicKey = File.ReadAllBytes(publicPath);
            var keyIdText = File.ReadAllText(keyIdPath).Trim();
            if (privateSeed.Length != Ed25519SeedSize) return false;
            if (publicKey.Length != Ed25519PublicKeySize) return false;
            if (keyIdText.Length != KeyIdHexLength) return false;

            var loadedKey = NsecKey.Import(
                SignatureAlgorithm.Ed25519, privateSeed, KeyBlobFormat.RawPrivateKey);
            // Cross-check: derive the keyId from the on-disk pubkey and confirm
            // it matches the stored keyId file. Mismatch means tampered-or-half-
            // written triple; treat as malformed and regenerate.
            var derivedKeyId = ComputeKeyId(publicKey);
            if (!string.Equals(derivedKeyId, keyIdText, StringComparison.Ordinal)) {
                loadedKey.Dispose();
                return false;
            }
            _key = loadedKey;
            _publicKey = publicKey;
            _keyId = keyIdText;
            return true;
        } catch (Exception ex) {
            _logger.LogWarning(ex,
                "Failed to load existing checkpoint key from {Folder}; will regenerate",
                _keyFolder);
            return false;
        }
    }

    private void Generate(string privatePath, string publicPath, string keyIdPath) {
        var parameters = new KeyCreationParameters {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        };
        var newKey = NsecKey.Create(SignatureAlgorithm.Ed25519, parameters);
        var privateSeed = newKey.Export(KeyBlobFormat.RawPrivateKey);
        var publicKey = newKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var keyId = ComputeKeyId(publicKey);

        // Atomic write via tmp + rename. If the process dies mid-write, the
        // final files remain consistent with the previous state (or absent).
        WriteFileAtomic(privatePath, privateSeed);
        WriteFileAtomic(publicPath, publicKey);
        WriteFileAtomic(keyIdPath, System.Text.Encoding.UTF8.GetBytes(keyId));

        _key = newKey;
        _publicKey = publicKey;
        _keyId = keyId;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> atomically
    /// (tmp file + rename), applying <c>0600</c> Unix mode on Linux. On
    /// Windows the inherited folder ACLs do the access-restriction job; the
    /// SetUnixFileMode call is silently ignored on Windows builds (the API
    /// throws PlatformNotSupportedException there — guarded with
    /// <see cref="OperatingSystem.IsWindows"/>).
    /// </summary>
    private static void WriteFileAtomic(string path, byte[] content) {
        var tmpPath = path + ".tmp";
        // Ensure no stale tmp from a prior crashed write.
        if (File.Exists(tmpPath)) File.Delete(tmpPath);
        File.WriteAllBytes(tmpPath, content);
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(tmpPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(tmpPath, path, overwrite: true);
    }

    private static string ComputeKeyId(byte[] publicKey) {
        var hash = SHA256.HashData(publicKey);
        return Convert.ToHexString(hash, 0, KeyIdHexLength / 2).ToLowerInvariant();
    }
}
