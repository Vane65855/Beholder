using System.Net;

namespace Beholder.Core;

/// <summary>
/// Write-side complement to <see cref="IDnsCache"/>. Lets enrichment paths
/// that learn a (hostname, IP) pair out-of-band — reverse-DNS fallback,
/// future preload sources — push the pair into the cache without depending on
/// a concrete cache implementation. Callers that only need to read the cache
/// continue to depend on <see cref="IDnsCache"/> alone.
/// </summary>
public interface IDnsCacheIngest {
    /// <summary>
    /// Records that <paramref name="queryName"/> currently resolves to
    /// <paramref name="address"/>. Last-writer-wins: a subsequent call for
    /// the same address overwrites the prior hostname. Thread-safe.
    /// </summary>
    void IngestResolved(string queryName, IPAddress address);
}
