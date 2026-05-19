namespace Beholder.Core.Discovery;

/// <summary>
/// Parses RFC 6762 / RFC 1035 mDNS response packets, extracting the first
/// PTR record's hostname. Mirrors <c>Beholder.Core.Tls.TlsClientHelloParser</c>'s
/// defensive shape: bounds-check every length field, return
/// <see langword="false"/> with no allocation and no exception on any
/// malformed input. DNS name compression handling and the forward-pointer
/// loop guard live in the shared <see cref="DnsNameDecoder"/> helper
/// (extracted in Phase 9.2.6 so the new service-discovery parser can
/// reuse it).
/// </summary>
public static class MdnsPacketParser {
    private const ushort RecordTypePtr = 0x000C;
    private const int DnsHeaderLength = 12;

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
            if (!DnsNameDecoder.TrySkipName(packet, ref p)) return false;
            if (p + 4 > packet.Length) return false;  // QTYPE + QCLASS
            p += 4;
        }

        // Walk the answer section. Return the first PTR target.
        for (var i = 0; i < answerCount; i++) {
            if (!DnsNameDecoder.TrySkipName(packet, ref p)) return false;  // NAME
            if (p + 10 > packet.Length) return false;                        // TYPE + CLASS + TTL + RDLENGTH

            var recordType = ReadU16(packet, p);
            // p+2 = class, p+4..p+8 = TTL, p+8..p+10 = rdlength
            var rdLength = ReadU16(packet, p + 8);
            p += 10;
            if (p + rdLength > packet.Length) return false;

            if (recordType == RecordTypePtr) {
                var rdataStart = p;
                if (!DnsNameDecoder.TryReadName(packet, ref rdataStart, out var name)) {
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

    private static string StripTrailingDot(string name) =>
        name.EndsWith('.') ? name[..^1] : name;

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);
}
