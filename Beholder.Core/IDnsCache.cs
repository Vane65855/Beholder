using System.Net;

namespace Beholder.Core;

/// <summary>
/// Read-only reverse lookup from an observed remote IP address to the hostname that
/// was most recently queried and resolved to that address on this machine. Populated
/// out-of-band by a platform-specific DNS observer (ETW on Windows). Consumers call
/// <see cref="Resolve"/> to enrich flow telemetry and UI snapshots with the user-
/// intended hostname, which for CDN traffic is strictly more informative than
/// reverse DNS (reverse DNS on CDN IPs returns generic edge names like
/// <c>server-52-84-150-39.fra2.r.cloudfront.net</c>).
/// </summary>
public interface IDnsCache {
    /// <summary>
    /// Returns the most recently observed hostname for the given IP address, or
    /// <c>null</c> if no DNS query has been seen that resolved to this address.
    /// Thread-safe. Synchronous because the lookup is an in-memory dictionary hit.
    /// </summary>
    string? Resolve(IPAddress address);
}
