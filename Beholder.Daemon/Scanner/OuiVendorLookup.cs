using Beholder.Core;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Scanner;

/// <summary>
/// IEEE OUI registry lookup. Loads the CSV at construction into an in-memory
/// dictionary; <see cref="GetVendor"/> normalizes input and serves from the
/// dictionary. Missing file degrades to an empty dictionary with a single
/// Warning log — every lookup returns null but the daemon stays functional
/// (matches the existing <c>NullGeoIpResolver</c> graceful-degradation posture).
/// </summary>
internal sealed class OuiVendorLookup : IOuiVendorLookup {
    private const int OuiPrefixHexLength = 6;

    private readonly IReadOnlyDictionary<string, string> _table;
    private readonly ILogger<OuiVendorLookup> _logger;

    public OuiVendorLookup(string ouiCsvPath, ILogger<OuiVendorLookup> logger) {
        ArgumentException.ThrowIfNullOrWhiteSpace(ouiCsvPath);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        if (!File.Exists(ouiCsvPath)) {
            _logger.LogWarning(
                "OUI file not found at {Path}, vendor lookups will return null", ouiCsvPath);
            _table = new Dictionary<string, string>(0);
            return;
        }

        using var stream = File.OpenRead(ouiCsvPath);
        using var reader = new StreamReader(stream);
        _table = OuiCsvParser.Parse(reader);
        _logger.LogInformation("Loaded {Count} OUI prefixes from {Path}", _table.Count, ouiCsvPath);
    }

    public string? GetVendor(string mac) {
        if (string.IsNullOrEmpty(mac)) return null;

        var prefix = ExtractPrefix(mac);
        if (prefix is null) return null;
        return _table.TryGetValue(prefix, out var vendor) ? vendor : null;
    }

    private static string? ExtractPrefix(string mac) {
        Span<char> buffer = stackalloc char[OuiPrefixHexLength];
        var written = 0;
        foreach (var ch in mac) {
            if (ch is ':' or '-' or '.') continue;
            if (!IsHex(ch)) return null;
            buffer[written++] = char.ToUpperInvariant(ch);
            if (written == OuiPrefixHexLength) return new string(buffer);
        }
        return null;
    }

    private static bool IsHex(char ch) =>
        ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
