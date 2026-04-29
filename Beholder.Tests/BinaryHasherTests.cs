using System.Security.Cryptography;
using Beholder.Daemon.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

public sealed class BinaryHasherTests : IDisposable {
    private readonly string _tempDir;

    public BinaryHasherTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "beholder-hasher-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ComputeAsync_KnownContent_ReturnsExpectedSha256() {
        var path = Path.Combine(_tempDir, "known.bin");
        var content = new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F };  // "hello"
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
        var expected = SHA256.HashData(content);

        var actual = await BinaryHasher.ComputeAsync(
            path, TimeSpan.FromSeconds(5),
            NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ComputeAsync_FileNotFound_ReturnsNull() {
        var path = Path.Combine(_tempDir, "does-not-exist.bin");

        var actual = await BinaryHasher.ComputeAsync(
            path, TimeSpan.FromSeconds(5),
            NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Null(actual);
    }

    [Fact]
    public async Task ComputeAsync_DirectoryNotFound_ReturnsNull() {
        var path = Path.Combine(_tempDir, "missing-dir", "file.bin");

        var actual = await BinaryHasher.ComputeAsync(
            path, TimeSpan.FromSeconds(5),
            NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Null(actual);
    }

    [Fact]
    public async Task ComputeAsync_NullPath_Throws() {
        await Assert.ThrowsAsync<ArgumentException>(() => BinaryHasher.ComputeAsync(
            "", TimeSpan.FromSeconds(5),
            NullLogger.Instance, CancellationToken.None));
    }
}
