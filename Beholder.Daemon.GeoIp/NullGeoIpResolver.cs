using System.Net;
using Beholder.Core;

namespace Beholder.Daemon.GeoIp;

/// <summary>
/// Pass-through <see cref="IGeoIpResolver"/> that returns
/// <see cref="CountryCode.Unknown"/> for every input. Used at daemon startup
/// when the DB-IP MMDB file is not present on disk, so the flow pipeline can
/// still run (with no country enrichment) instead of crashing.
/// </summary>
public sealed class NullGeoIpResolver : IGeoIpResolver {
    public CountryCode Resolve(IPAddress address) => CountryCode.Unknown;
}
