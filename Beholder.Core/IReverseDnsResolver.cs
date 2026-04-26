using System.Net;

namespace Beholder.Core;

/// <summary>
/// Issues PTR (reverse-DNS) lookups for IP addresses that have no observed
/// hostname from the platform's passive DNS observer (Windows DNS Client ETW
/// on Windows). Used as the final fallback by the reverse-DNS decorator over
/// <see cref="IDnsCache"/>; see ADR 005.
/// </summary>
/// <remarks>
/// Implementations must not throw on the expected failure modes (no PTR
/// record, network timeout, transient resolver failure). Returning <c>null</c>
/// matches the same "skip path is silent" idiom as <see cref="IDnsCache.Resolve"/>
/// and lets the decorator distinguish "not yet attempted" (caller logic) from
/// "attempted and failed" (negative-cache write) without exception handling on
/// the decorator's hot path.
/// </remarks>
public interface IReverseDnsResolver {
    /// <summary>
    /// Returns the canonical hostname registered in DNS for
    /// <paramref name="address"/>, or <c>null</c> if the address has no PTR
    /// record, the query times out, or any network error occurs.
    /// </summary>
    ValueTask<string?> ResolveAsync(IPAddress address, CancellationToken cancellationToken);
}
