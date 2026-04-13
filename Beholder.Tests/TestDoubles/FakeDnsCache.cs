using System.Net;
using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

internal sealed class FakeDnsCache : IDnsCache {
    private readonly Dictionary<string, string> _entries = new();

    public void Add(string ipAddress, string hostname) {
        _entries[ipAddress] = hostname;
    }

    public string? Resolve(IPAddress address) {
        return _entries.TryGetValue(address.ToString(), out var hostname) ? hostname : null;
    }
}
