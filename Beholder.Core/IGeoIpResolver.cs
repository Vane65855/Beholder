using System.Net;

namespace Beholder.Core;

/// <summary>
/// Resolves IP addresses to ISO 3166-1 alpha-2 country codes. Synchronous because the
/// backing MMDB is memory-mapped and lookups complete in well under a microsecond,
/// which lets the daemon's hot path call this without an async hop.
/// </summary>
public interface IGeoIpResolver {
    /// <summary>
    /// Resolves the given address to a country code. Returns
    /// <see cref="CountryCode.Local"/> for private/reserved ranges (no MMDB lookup),
    /// <see cref="CountryCode.Unknown"/> for addresses not present in the database,
    /// and the real alpha-2 code for everything else.
    /// </summary>
    CountryCode Resolve(IPAddress address);
}
