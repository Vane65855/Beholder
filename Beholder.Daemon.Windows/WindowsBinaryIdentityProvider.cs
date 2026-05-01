using Beholder.Core;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows;

/// <summary>
/// Windows implementation of <see cref="IBinaryIdentityProvider"/>. Composes
/// <see cref="PeVersionInfoReader"/> (CompanyName + ProductName from PE
/// VersionInfo) and <see cref="AuthenticodeVerifier"/> (Subject CN + Issuer
/// CN + chain validation status from WinVerifyTrust) into a single
/// <see cref="BinaryIdentity"/> result. See ADR 007.
/// </summary>
/// <remarks>
/// Both reads are best-effort. A binary missing VersionInfo still gets an
/// AuthenticodeInfo if signed, and vice versa — the alert pipeline's
/// fallback chain sorts out whether the available metadata is enough to
/// drive logical-identity dedup. Per-call exceptions are caught and logged
/// at Warning so a single bad binary doesn't take down the detector.
/// </remarks>
public sealed class WindowsBinaryIdentityProvider : IBinaryIdentityProvider {
    private readonly ILogger<WindowsBinaryIdentityProvider> _logger;

    public WindowsBinaryIdentityProvider(ILogger<WindowsBinaryIdentityProvider> logger) {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<BinaryIdentity?> ReadIdentityAsync(string path, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        // Both reads are synchronous Win32 calls. Wrapping in a Task lets
        // the interface be async-friendly without forcing a thread hop —
        // callers already run on the engine consumer thread or a Task.Run
        // continuation, so a synchronous body is fine.
        try {
            if (!File.Exists(path)) {
                _logger.LogWarning(
                    "WindowsBinaryIdentityProvider: file does not exist at {Path}", path);
                return Task.FromResult<BinaryIdentity?>(null);
            }

            var (company, product) = PeVersionInfoReader.Read(path, _logger);
            var signature = AuthenticodeVerifier.Read(path, _logger);

            // Even when both VersionInfo and signature are absent, return a
            // non-null BinaryIdentity. The detector treats nulls as "no
            // identity available" and falls back to path-based dedup. Only
            // return null itself when the file genuinely can't be read.
            return Task.FromResult<BinaryIdentity?>(
                new BinaryIdentity(company, product, signature));
        } catch (Exception ex) {
            _logger.LogWarning(ex,
                "WindowsBinaryIdentityProvider: failed to read identity for {Path}", path);
            return Task.FromResult<BinaryIdentity?>(null);
        }
    }
}
