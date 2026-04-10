using System.Net;

namespace Beholder.Core;

/// <summary>
/// Extension methods for <see cref="IPAddress"/> used by the daemon's flow pipeline.
/// </summary>
public static class IPAddressExtensions {
    /// <summary>
    /// Returns true when the address falls inside an RFC-defined private, loopback,
    /// link-local, ULA, or shared (CGNAT) range. Used by the GeoIP resolver to
    /// short-circuit MMDB lookups for traffic that has no meaningful country.
    /// </summary>
    public static bool IsPrivateOrReserved(this IPAddress address) {
        ArgumentNullException.ThrowIfNull(address);
        // Hard-coded RFC 1918 / 4193 / 5737 / 6598 ranges. The hot path runs this on
        // every flow event, so we avoid string parsing and walk raw bytes instead.
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) return IsPrivateOrReservedIPv4(bytes);
        if (bytes.Length == 16) return IsPrivateOrReservedIPv6(bytes);
        return false;
    }

    private static bool IsPrivateOrReservedIPv4(ReadOnlySpan<byte> bytes) {
        var first = bytes[0];
        var second = bytes[1];
        if (first == 10) return true;
        if (first == 127) return true;
        if (first == 172 && second >= 16 && second <= 31) return true;
        if (first == 192 && second == 168) return true;
        if (first == 169 && second == 254) return true;
        if (first == 100 && second >= 64 && second <= 127) return true;
        return false;
    }

    private static bool IsPrivateOrReservedIPv6(ReadOnlySpan<byte> bytes) {
        if (IsIPv4Mapped(bytes)) return IsPrivateOrReservedIPv4(bytes[12..]);
        if (IsIPv6Loopback(bytes)) return true;
        if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return true;
        if ((bytes[0] & 0xfe) == 0xfc) return true;
        return false;
    }

    private static bool IsIPv4Mapped(ReadOnlySpan<byte> bytes) {
        for (var i = 0; i < 10; i++) {
            if (bytes[i] != 0) return false;
        }
        return bytes[10] == 0xff && bytes[11] == 0xff;
    }

    private static bool IsIPv6Loopback(ReadOnlySpan<byte> bytes) {
        for (var i = 0; i < 15; i++) {
            if (bytes[i] != 0) return false;
        }
        return bytes[15] == 1;
    }
}
