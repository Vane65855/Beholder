using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IBinaryIdentityProvider"/>. Maps process paths
/// to controllable <see cref="BinaryIdentity"/> values so detector tests can
/// drive the Phase 7.5 logical-identity dedup and spoof-detection paths
/// without spinning up real PE files or Authenticode certs.
/// </summary>
internal sealed class FakeBinaryIdentityProvider : IBinaryIdentityProvider {
    private readonly Dictionary<string, BinaryIdentity?> _identities =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pre-loads <paramref name="identity"/> as the result for any future
    /// ReadIdentityAsync call against <paramref name="path"/>. Pass null to
    /// simulate a file the provider can't read at all.
    /// </summary>
    public void Set(string path, BinaryIdentity? identity) {
        _identities[path] = identity;
    }

    public Task<BinaryIdentity?> ReadIdentityAsync(string path, CancellationToken cancellationToken) {
        _identities.TryGetValue(path, out var identity);
        return Task.FromResult(identity);
    }
}
