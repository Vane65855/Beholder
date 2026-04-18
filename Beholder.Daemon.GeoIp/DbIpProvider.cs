using System.Net;
using Beholder.Core;
using MaxMind.Db;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.GeoIp;

/// <summary>
/// Resolves IP addresses to ISO 3166-1 alpha-2 country codes using a DB-IP Lite
/// MMDB file. Private and reserved ranges are short-circuited to
/// <see cref="CountryCode.Local"/> without hitting the database. Results are
/// memoized in an in-process LRU cache capped at 10,000 entries.
/// </summary>
public sealed class DbIpProvider : IGeoIpResolver, IDisposable {
    private const int CacheEntryLimit = 10_000;

    private readonly Reader _reader;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DbIpProvider> _logger;

    public DbIpProvider(string mmdbFilePath, ILogger<DbIpProvider> logger) {
        ArgumentException.ThrowIfNullOrWhiteSpace(mmdbFilePath);
        ArgumentNullException.ThrowIfNull(logger);
        if (!File.Exists(mmdbFilePath)) {
            throw new FileNotFoundException(
                $"DB-IP MMDB file not found at {mmdbFilePath}", mmdbFilePath);
        }
        _reader = new Reader(mmdbFilePath);
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = CacheEntryLimit });
        _logger = logger;
    }

    public CountryCode Resolve(IPAddress address) {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsPrivateOrReserved()) return CountryCode.Local;
        return _cache.GetOrCreate(address, entry => {
            entry.Size = 1;
            return ResolveFromMmdb(address);
        });
    }

    public void Dispose() {
        _reader.Dispose();
        _cache.Dispose();
    }

    private CountryCode ResolveFromMmdb(IPAddress address) {
        try {
            var record = _reader.Find<Dictionary<string, object>>(address);
            if (record is null) return CountryCode.Unknown;
            if (!record.TryGetValue("country", out var countryObj)) return CountryCode.Unknown;
            if (countryObj is not Dictionary<string, object> countryDict) return CountryCode.Unknown;
            if (!countryDict.TryGetValue("iso_code", out var isoObj)) return CountryCode.Unknown;
            if (isoObj is not string iso || string.IsNullOrWhiteSpace(iso)) return CountryCode.Unknown;
            return CountryCode.FromAlpha2(iso);
        } catch (Exception ex) {
            // Graceful degradation: any MMDB anomaly (corrupted file, unexpected
            // record schema, reader state error, unknown future MaxMind.Db
            // exception types) collapses to CountryCode.Unknown. A per-address
            // resolve failure is not a daemon-level fault — the UI renders the
            // flow as country "??" and the rest of the pipeline continues.
            _logger.LogWarning(ex,
                "MMDB lookup failed for {Address}, returning Unknown", address);
            return CountryCode.Unknown;
        }
    }
}
