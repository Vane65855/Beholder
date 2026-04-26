using System.Collections.Concurrent;
using System.Net;
using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Implements both <see cref="IDnsCache"/> and <see cref="IDnsCacheIngest"/>
/// so tests for decorators that wrap a cache and write results back into it
/// (e.g. <c>ReverseDnsFallbackCache</c>) can use a single fake for both
/// roles. Backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/> so
/// concurrent reads from the consumer and writes from the worker thread
/// don't race.
/// </summary>
internal sealed class FakeDnsCache : IDnsCache, IDnsCacheIngest {
    private readonly ConcurrentDictionary<string, string> _entries = new();

    /// <summary>Test setup helper. Same effect as <see cref="IngestResolved"/> but
    /// keyed by IP-string for compatibility with existing tests.</summary>
    public void Add(string ipAddress, string hostname) {
        _entries[ipAddress] = hostname;
    }

    public string? Resolve(IPAddress address) {
        return _entries.TryGetValue(address.ToString(), out var hostname) ? hostname : null;
    }

    public void IngestResolved(string queryName, IPAddress address) {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryName);
        ArgumentNullException.ThrowIfNull(address);
        _entries[address.ToString()] = queryName;
    }
}
