using System.Text;

namespace Beholder.Core.Discovery;

/// <summary>
/// Defensive DNS name codec shared between the mDNS reverse-PTR parser
/// (<see cref="MdnsPacketParser"/>) and the mDNS service-discovery parser
/// (<see cref="MdnsServiceDiscoveryParser"/>). Handles RFC 1035 §4.1.4
/// name-pointer compression with a forward-pointer loop guard and a
/// bounded compression-hop ceiling so adversarial input cannot trigger
/// infinite recursion or unbounded memory growth.
/// </summary>
/// <remarks>
/// Every method returns <see langword="false"/> on malformed input rather
/// than throwing. The contract matches
/// <see cref="Beholder.Core.Tls.TlsClientHelloParser"/>: parsers are trust
/// boundaries that absorb bad input silently — callers should treat
/// failure as "skip this record" rather than "abort the parse."
/// </remarks>
internal static class DnsNameDecoder {
    private const byte NameCompressionPointerMask = 0xC0;
    private const int MaxNameLength = 255;             // RFC 1035 §2.3.4
    private const int MaxLabelLength = 63;             // RFC 1035 §2.3.4 + §4.1.4
    private const int MaxCompressionPointerHops = 16;  // defensive ceiling

    /// <summary>
    /// Walks past a DNS-encoded name in <paramref name="packet"/> starting
    /// at <paramref name="offset"/> without decoding it. Updates
    /// <paramref name="offset"/> to point past the encoded name. Handles
    /// 2-byte compression pointers (which we don't follow when skipping —
    /// we just advance past the pointer's 2 bytes). Returns false on
    /// malformed input (oversize label, truncated buffer, etc.).
    /// </summary>
    public static bool TrySkipName(ReadOnlySpan<byte> packet, ref int offset) {
        var totalLabelBytes = 0;
        while (offset < packet.Length) {
            var len = packet[offset];
            if (len == 0) {
                offset += 1;
                return true;
            }
            if ((len & NameCompressionPointerMask) == NameCompressionPointerMask) {
                if (offset + 2 > packet.Length) return false;
                offset += 2;
                return true;
            }
            if (len > MaxLabelLength) return false;
            if (offset + 1 + len > packet.Length) return false;
            totalLabelBytes += 1 + len;
            if (totalLabelBytes > MaxNameLength) return false;
            offset += 1 + len;
        }
        return false;
    }

    /// <summary>
    /// Reads a DNS-encoded name from <paramref name="packet"/> starting
    /// at <paramref name="offset"/> into a string. Updates
    /// <paramref name="offset"/> to point past the encoded name in the
    /// ORIGINAL buffer (NOT past any pointer targets). Follows compression
    /// pointers per RFC 1035 §4.1.4 with a forward-pointer guard (target
    /// offset must be strictly less than the current cursor — prevents
    /// infinite loops on malformed forward references).
    ///
    /// <para>
    /// When <paramref name="strict"/> is true (default), each label byte
    /// must be a "DNS-safe" character: ASCII letter, digit, hyphen,
    /// underscore, or dot. Non-conforming bytes cause the name to be
    /// rejected. When false, printable ASCII (0x20-0x7E) is allowed,
    /// which permits service-instance names with spaces — the DNS-SD
    /// (RFC 6763 §4.1.1) convention.
    /// </para>
    /// </summary>
    public static bool TryReadName(
        ReadOnlySpan<byte> packet, ref int offset, out string? name, bool strict = true
    ) {
        name = null;
        var builder = new StringBuilder();
        var hops = 0;
        var cursor = offset;
        var hopBoundary = -1;  // when we first follow a pointer, the caller's offset stops here

        while (cursor < packet.Length) {
            var len = packet[cursor];
            if (len == 0) {
                if (hopBoundary < 0) offset = cursor + 1;
                name = builder.ToString();
                return true;
            }
            if ((len & NameCompressionPointerMask) == NameCompressionPointerMask) {
                if (cursor + 2 > packet.Length) return false;
                if (hops >= MaxCompressionPointerHops) return false;
                if (hopBoundary < 0) {
                    hopBoundary = cursor + 2;
                    offset = hopBoundary;
                }

                var target = ((len & 0x3F) << 8) | packet[cursor + 1];
                if (target >= cursor) return false;  // forward pointer = loop guard
                cursor = target;
                hops++;
                continue;
            }
            if (len > MaxLabelLength) return false;
            if (cursor + 1 + len > packet.Length) return false;

            if (builder.Length + len + 1 > MaxNameLength) return false;
            if (builder.Length > 0) builder.Append('.');
            for (var i = 0; i < len; i++) {
                var ch = packet[cursor + 1 + i];
                if (strict ? !IsDnsSafe(ch) : !IsPrintableAscii(ch)) return false;
                builder.Append((char)ch);
            }
            cursor += 1 + len;
        }
        return false;
    }

    /// <summary>
    /// Returns the FIRST label of the DNS-encoded name at
    /// <paramref name="offset"/> without advancing the offset and without
    /// following compression pointers further than one hop. Allows
    /// printable ASCII including spaces — matches the DNS-SD instance-name
    /// convention where a label like <c>"Living Room TV"</c> appears as a
    /// single label with literal space bytes.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="MdnsServiceDiscoveryParser"/> as a fallback when
    /// neither SRV target nor A record owner is available — the PTR
    /// instance's leftmost label is often a human-friendly name even
    /// when no companion records are present.
    /// </remarks>
    public static bool TryReadFirstLabel(ReadOnlySpan<byte> packet, int offset, out string? label) {
        label = null;
        if (offset < 0 || offset >= packet.Length) return false;

        // Follow one compression pointer hop if present, then read the
        // first label as raw bytes.
        var len = packet[offset];
        if ((len & NameCompressionPointerMask) == NameCompressionPointerMask) {
            if (offset + 2 > packet.Length) return false;
            var target = ((len & 0x3F) << 8) | packet[offset + 1];
            if (target >= offset) return false;  // forward-pointer guard
            offset = target;
            if (offset >= packet.Length) return false;
            len = packet[offset];
            // Don't follow a chained pointer here — keep this helper simple.
            if ((len & NameCompressionPointerMask) == NameCompressionPointerMask) return false;
        }

        if (len == 0) return false;  // empty name = no labels
        if (len > MaxLabelLength) return false;
        if (offset + 1 + len > packet.Length) return false;

        var builder = new StringBuilder(len);
        for (var i = 0; i < len; i++) {
            var ch = packet[offset + 1 + i];
            if (!IsPrintableAscii(ch)) return false;
            builder.Append((char)ch);
        }
        label = builder.ToString();
        return true;
    }

    private static bool IsDnsSafe(byte b) {
        if (b is (byte)'-' or (byte)'_' or (byte)'.') return true;
        if (b >= (byte)'0' && b <= (byte)'9') return true;
        if (b >= (byte)'A' && b <= (byte)'Z') return true;
        if (b >= (byte)'a' && b <= (byte)'z') return true;
        return false;
    }

    private static bool IsPrintableAscii(byte b) => b is >= 0x20 and <= 0x7E;
}
