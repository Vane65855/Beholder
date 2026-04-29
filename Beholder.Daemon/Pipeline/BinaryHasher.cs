using System.Security.Cryptography;

namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Computes SHA-256 of a binary on disk with the defensive concerns the
/// Phase 7 monitor needs: a per-file timeout (so an unusually large file or
/// a stuck antivirus scan doesn't stall the rest of the registry sweep) and
/// explicit catches for the noise cases (file went away, daemon's service
/// account can't read it).
/// </summary>
internal static class BinaryHasher {
    /// <summary>
    /// Reads the file at <paramref name="path"/> with <c>FileShare.Read</c>
    /// (antivirus often holds a read-shared lock during its own scan) and
    /// returns its SHA-256. Returns null on any of: file-not-found,
    /// access-denied, timeout. Each null path is logged at Warning so a
    /// noisy environment is visible to the operator.
    /// </summary>
    public static async Task<byte[]?> ComputeAsync(
        string path,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            return await SHA256.HashDataAsync(stream, linkedCts.Token).ConfigureAwait(false);
        } catch (FileNotFoundException) {
            logger.LogWarning("BinaryHasher: file not found at {Path}", path);
            return null;
        } catch (DirectoryNotFoundException) {
            logger.LogWarning("BinaryHasher: directory not found for {Path}", path);
            return null;
        } catch (UnauthorizedAccessException) {
            logger.LogWarning("BinaryHasher: access denied to {Path}", path);
            return null;
        } catch (IOException ex) {
            logger.LogWarning(ex, "BinaryHasher: I/O error reading {Path}", path);
            return null;
        } catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested) {
            logger.LogWarning(
                "BinaryHasher: timed out hashing {Path} after {Timeout}",
                path, timeout);
            return null;
        }
    }
}
