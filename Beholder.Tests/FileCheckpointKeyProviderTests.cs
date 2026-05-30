using Beholder.Daemon.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public sealed class FileCheckpointKeyProviderTests : IDisposable {
    private readonly string _tempDir;

    public FileCheckpointKeyProviderTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-tests", Guid.NewGuid().ToString());
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Constructor_NullOrWhitespaceFolder_Throws() {
        Assert.Throws<ArgumentException>(() =>
            new FileCheckpointKeyProvider("", NullLogger<FileCheckpointKeyProvider>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_Throws() {
        Assert.Throws<ArgumentNullException>(() =>
            new FileCheckpointKeyProvider(_tempDir, null!));
    }

    [Fact]
    public void FirstAccess_NoExistingFiles_GeneratesKeypairAndWritesFiles() {
        using var provider = new FileCheckpointKeyProvider(
            _tempDir, NullLogger<FileCheckpointKeyProvider>.Instance);

        var keyId = provider.KeyId;
        var publicKey = provider.PublicKey;

        Assert.Equal(16, keyId.Length);
        Assert.Matches("^[0-9a-f]{16}$", keyId);
        Assert.Equal(32, publicKey.Length);
        Assert.True(File.Exists(Path.Combine(_tempDir, "checkpoint-private.bin")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "checkpoint-public.bin")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "checkpoint-key-id.txt")));
    }

    [Fact]
    public void SecondProvider_OverSamefolder_LoadsExistingKey() {
        string firstKeyId;
        byte[] firstPubKey;
        using (var first = new FileCheckpointKeyProvider(
            _tempDir, NullLogger<FileCheckpointKeyProvider>.Instance)) {
            firstKeyId = first.KeyId;
            firstPubKey = first.PublicKey.ToArray();
        }

        using var second = new FileCheckpointKeyProvider(
            _tempDir, NullLogger<FileCheckpointKeyProvider>.Instance);

        Assert.Equal(firstKeyId, second.KeyId);
        Assert.Equal(firstPubKey, second.PublicKey.ToArray());
    }

    [Fact]
    public void Sign_AndVerify_RoundTrip() {
        using var provider = new FileCheckpointKeyProvider(
            _tempDir, NullLogger<FileCheckpointKeyProvider>.Instance);
        var data = "the chain looked like this at time T"u8.ToArray();

        var signature = provider.Sign(data);

        Assert.Equal(64, signature.Length);
        Assert.True(provider.Verify(data, signature));
    }

    [Fact]
    public void Verify_TamperedData_ReturnsFalse() {
        using var provider = new FileCheckpointKeyProvider(
            _tempDir, NullLogger<FileCheckpointKeyProvider>.Instance);
        var data = new byte[] { 0x01, 0x02, 0x03 };
        var signature = provider.Sign(data);

        // Flip a single bit in the data.
        data[0] ^= 0xFF;

        Assert.False(provider.Verify(data, signature));
    }

    [Fact]
    public void Verify_TamperedSignature_ReturnsFalse() {
        using var provider = new FileCheckpointKeyProvider(
            _tempDir, NullLogger<FileCheckpointKeyProvider>.Instance);
        var data = new byte[] { 0x01, 0x02, 0x03 };
        var signature = provider.Sign(data);

        signature[0] ^= 0xFF;

        Assert.False(provider.Verify(data, signature));
    }

    [Fact]
    public void CorruptedPrivateKeyFile_RegeneratesKeypair() {
        // Set up a valid keypair first.
        string firstKeyId;
        using (var first = new FileCheckpointKeyProvider(
            _tempDir, NullLogger<FileCheckpointKeyProvider>.Instance)) {
            firstKeyId = first.KeyId;
        }
        // Truncate the private-key file to an invalid length.
        File.WriteAllBytes(Path.Combine(_tempDir, "checkpoint-private.bin"), new byte[] { 0xFF });

        using var second = new FileCheckpointKeyProvider(
            _tempDir, NullLogger<FileCheckpointKeyProvider>.Instance);

        // A fresh keypair was generated — the new keyId differs (overwhelmingly
        // likely; the probability of accidental collision is ~2^-64).
        Assert.NotEqual(firstKeyId, second.KeyId);
        Assert.True(File.Exists(Path.Combine(_tempDir, "checkpoint-private.bin")));
        Assert.Equal(32, new FileInfo(Path.Combine(_tempDir, "checkpoint-private.bin")).Length);
    }

    [Fact]
    public void KeyIdMismatchBetweenPubKeyAndKeyIdFile_RegeneratesKeypair() {
        // Generate a valid set first.
        using (var first = new FileCheckpointKeyProvider(
            _tempDir, NullLogger<FileCheckpointKeyProvider>.Instance)) {
            _ = first.KeyId;
        }
        // Hand-tamper the key-id file so it no longer matches the pubkey hash.
        File.WriteAllText(Path.Combine(_tempDir, "checkpoint-key-id.txt"), "0000000000000000");

        using var second = new FileCheckpointKeyProvider(
            _tempDir, NullLogger<FileCheckpointKeyProvider>.Instance);

        // The provider detects the mismatch and regenerates — the new keyId
        // is computed from a freshly-generated pubkey, so it can't equal the
        // hand-tampered "0000…" value.
        Assert.NotEqual("0000000000000000", second.KeyId);
    }
}
