using System.Text;

namespace Beholder.Core.Discovery;

/// <summary>
/// Parses RFC 6762 / RFC 1035 mDNS response packets, extracting the first
/// PTR record's hostname. Mirrors <c>Beholder.Core.Tls.TlsClientHelloParser</c>'s
/// defensive shape: bounds-check every length field, return
/// <see langword="false"/> with no allocation and no exception on any
/// malformed input.
/// </summary>
/// <remarks>
/// Handles DNS name compression per RFC 1035 §4.1.4: a length byte with the
/// top 2 bits set (≥ 0xC0) is a pointer to an offset elsewhere in the
/// message. The parser bounds-checks pointer targets and limits the
/// dereference depth to prevent infinite loops on malformed compression.
/// </remarks>
public static class MdnsPacketParser {
    private const ushort RecordTypePtr = 0x000C;
    private const int DnsHeaderLength = 12;
    private const byte NameCompressionPointerMask = 0xC0;
    private const int MaxNameLength = 255;             // RFC 1035 §2.3.4
    private const int MaxLabelLength = 63;             // RFC 1035 §2.3.4 + §4.1.4
    private const int MaxCompressionPointerHops = 16;  // defensive ceiling

    /// <summary>
    /// Attempts to extract the first PTR record's hostname from an mDNS
    /// response packet. Returns <see langword="true"/> with
    /// <paramref name="hostname"/> set when the packet has the expected
    /// transaction ID and at least one PTR answer with a non-empty RDATA
    /// name. Returns <see langword="false"/> in every other case (wrong TID,
    /// truncated, no PTR answers, malformed name compression, etc.).
    /// </summary>
    /// <param name="packet">The raw UDP payload received from the responder.</param>
    /// <param name="expectedTransactionId">
    /// The transaction ID the original query used. Responses with a
    /// different TID belong to a different query (or are spurious) and
    /// must be rejected.
    /// </param>
    /// <param name="hostname">
    /// On success, the decoded PTR target as an ASCII string with the
    /// trailing dot stripped (e.g. <c>"iPhone.local"</c>). On failure,
    /// <see langword="null"/>.
    /// </param>
    public static bool TryExtractHostname(
        ReadOnlySpan<byte> packet, ushort expectedTransactionId, out string? hostname
    ) {
        hostname = null;

        if (packet.Length < DnsHeaderLength) return false;
        if (ReadU16(packet, 0) != expectedTransactionId) return false;

        var answerCount = ReadU16(packet, 6);
        if (answerCount == 0) return false;

        // Skip the question section (we don't need to validate the QNAME — the
        // TID match is sufficient correlation per RFC 6762).
        var p = DnsHeaderLength;
        var questionCount = ReadU16(packet, 4);
        for (var i = 0; i < questionCount; i++) {
            if (!TrySkipName(packet, ref p)) return false;
            if (p + 4 > packet.Length) return false;  // QTYPE + QCLASS
            p += 4;
        }

        // Walk the answer section. Return the first PTR target.
        for (var i = 0; i < answerCount; i++) {
            if (!TrySkipName(packet, ref p)) return false;       // NAME
            if (p + 10 > packet.Length) return false;             // TYPE + CLASS + TTL + RDLENGTH

            var recordType = ReadU16(packet, p);
            // p+2 = class, p+4..p+8 = TTL, p+8..p+10 = rdlength
            var rdLength = ReadU16(packet, p + 8);
            p += 10;
            if (p + rdLength > packet.Length) return false;

            if (recordType == RecordTypePtr) {
                var rdataStart = p;
                if (!TryReadName(packet, ref rdataStart, out var name)) {
                    // Malformed RDATA in this answer — skip to next answer
                    // rather than failing the whole parse; a later answer
                    // might be well-formed.
                    p += rdLength;
                    continue;
                }
                if (string.IsNullOrEmpty(name)) {
                    p += rdLength;
                    continue;
                }
                hostname = StripTrailingDot(name);
                return true;
            }

            p += rdLength;
        }

        return false;
    }

    /// <summary>
    /// Walks past a DNS-encoded name without decoding it. Returns
    /// <see langword="false"/> on malformed input. Used to skip over the
    /// question section's QNAME and each answer's NAME field without
    /// allocating.
    /// </summary>
    private static bool TrySkipName(ReadOnlySpan<byte> packet, ref int offset) {
        var totalLabelBytes = 0;
        while (offset < packet.Length) {
            var len = packet[offset];
            if (len == 0) {
                offset += 1;
                return true;
            }
            if ((len & NameCompressionPointerMask) == NameCompressionPointerMask) {
                // 2-byte compression pointer — never followed inline when skipping.
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
        return false;  // ran off the end before seeing a terminator
    }

    /// <summary>
    /// Reads a DNS-encoded name into a string, following compression
    /// pointers per RFC 1035 §4.1.4. After the call, <paramref name="offset"/>
    /// is advanced past the encoded name in the original buffer (NOT past
    /// any pointer target).
    /// </summary>
    private static bool TryReadName(
        ReadOnlySpan<byte> packet, ref int offset, out string? name
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
                if (hopBoundary < 0) hopBoundary = cursor + 2;

                var target = ((len & 0x3F) << 8) | packet[cursor + 1];
                if (target >= cursor) return false;  // forward pointer = loop guard (must point earlier)
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
                // DNS labels are limited to letters, digits, hyphens, plus
                // underscore for some service names. Reject control / 8-bit
                // bytes that would indicate corruption.
                if (!IsAllowedNameByte(ch)) return false;
                builder.Append((char)ch);
            }
            cursor += 1 + len;
        }
        return false;
    }

    private static bool IsAllowedNameByte(byte b) {
        if (b == '-' || b == '_' || b == '.') return true;
        if (b >= '0' && b <= '9') return true;
        if (b >= 'A' && b <= 'Z') return true;
        if (b >= 'a' && b <= 'z') return true;
        return false;
    }

    private static string StripTrailingDot(string name) =>
        name.EndsWith('.') ? name[..^1] : name;

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);
}
