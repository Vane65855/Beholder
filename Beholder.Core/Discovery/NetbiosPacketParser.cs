using System.Text;

namespace Beholder.Core.Discovery;

/// <summary>
/// Parses RFC 1002 NetBIOS Node Status (NBSTAT) response packets, extracting
/// the host's workstation name. Mirrors
/// <c>Beholder.Core.Tls.TlsClientHelloParser</c>'s defensive shape:
/// bounds-check every length field, return <see langword="false"/> with no
/// allocation and no exception on any malformed input.
/// </summary>
/// <remarks>
/// NBSTAT response layout (after the 12-byte header):
///
/// <list type="bullet">
///   <item><b>Question section</b> (repeated QDCOUNT times in the response,
///     same encoding as the query): 1-byte length (0x20) + 32-byte encoded
///     name + 1-byte terminator + 2-byte QTYPE + 2-byte QCLASS = 38 bytes.</item>
///   <item><b>Answer section:</b> NAME (variable; may be a compression pointer
///     back to the question) + 2-byte TYPE + 2-byte CLASS + 4-byte TTL +
///     2-byte RDLENGTH + RDATA.</item>
///   <item><b>RDATA</b> for NBSTAT: 1-byte NUM_NAMES + (NUM_NAMES × 18-byte
///     entries) + 46-byte node statistics. Each name entry is a 15-byte
///     space-padded ASCII name + 1-byte suffix + 2-byte flags. The workstation
///     name has suffix <c>0x00</c> and is "unique" (high bit of flags clear).</item>
/// </list>
///
/// We scan the name entries looking for a unique workstation name and return
/// it with trailing spaces stripped.
/// </remarks>
public static class NetbiosPacketParser {
    private const int NbHeaderLength = 12;
    private const int NameEntryLength = 18;
    private const int NameAsciiLength = 15;
    private const byte WorkstationSuffix = 0x00;
    private const ushort GroupNameBit = 0x8000;
    private const byte NameCompressionPointerMask = 0xC0;

    /// <summary>
    /// Attempts to extract the host's NetBIOS workstation name from an
    /// NBSTAT response packet. Returns <see langword="true"/> with
    /// <paramref name="hostname"/> set when the packet has the expected
    /// transaction ID and contains at least one unique name with suffix
    /// <c>0x00</c>. Returns <see langword="false"/> in every other case
    /// (wrong TID, truncated, only group names, etc.).
    /// </summary>
    /// <param name="packet">The raw UDP payload received from the responder.</param>
    /// <param name="expectedTransactionId">
    /// The transaction ID the original query used. Responses with a
    /// different TID belong to a different query and must be rejected.
    /// </param>
    /// <param name="hostname">
    /// On success, the workstation name as an ASCII string with trailing
    /// spaces stripped (e.g. <c>"VANE-PC"</c>). On failure,
    /// <see langword="null"/>.
    /// </param>
    public static bool TryExtractHostname(
        ReadOnlySpan<byte> packet, ushort expectedTransactionId, out string? hostname
    ) {
        hostname = null;
        if (packet.Length < NbHeaderLength) return false;
        if (ReadU16(packet, 0) != expectedTransactionId) return false;

        var answerCount = ReadU16(packet, 6);
        if (answerCount == 0) return false;

        // Skip the question section (NBSTAT questions are always 38 bytes:
        // 1 + 32 + 1 + 2 + 2).
        var p = NbHeaderLength;
        var questionCount = ReadU16(packet, 4);
        for (var i = 0; i < questionCount; i++) {
            if (!TrySkipName(packet, ref p)) return false;
            if (p + 4 > packet.Length) return false;  // QTYPE + QCLASS
            p += 4;
        }

        // Walk the answer section. The first answer with NBSTAT-shaped RDATA
        // gives us the names list.
        for (var i = 0; i < answerCount; i++) {
            if (!TrySkipName(packet, ref p)) return false;       // NAME (often a compression pointer)
            if (p + 10 > packet.Length) return false;             // TYPE + CLASS + TTL + RDLENGTH

            var rdLength = ReadU16(packet, p + 8);
            p += 10;
            if (p + rdLength > packet.Length) return false;

            if (rdLength < 1) {
                p += rdLength;
                continue;
            }

            var numNames = packet[p];
            var namesStart = p + 1;
            var namesByteCount = numNames * NameEntryLength;
            if (namesByteCount > rdLength - 1) {
                p += rdLength;
                continue;
            }

            for (var j = 0; j < numNames; j++) {
                var entryOffset = namesStart + (j * NameEntryLength);
                if (entryOffset + NameEntryLength > packet.Length) return false;

                var suffix = packet[entryOffset + NameAsciiLength];
                var flags = ReadU16(packet, entryOffset + NameAsciiLength + 1);
                if (suffix != WorkstationSuffix) continue;
                if ((flags & GroupNameBit) != 0) continue;  // skip group entries

                var nameBytes = packet.Slice(entryOffset, NameAsciiLength);
                if (TryDecodeName(nameBytes, out var name)) {
                    hostname = name;
                    return true;
                }
            }

            p += rdLength;
        }

        return false;
    }

    /// <summary>
    /// Walks past a DNS-encoded name without decoding it. Handles
    /// 2-byte compression pointers (the typical case in NBSTAT answer
    /// NAMEs — they point back to the question).
    /// </summary>
    private static bool TrySkipName(ReadOnlySpan<byte> packet, ref int offset) {
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
            if (offset + 1 + len > packet.Length) return false;
            offset += 1 + len;
        }
        return false;
    }

    /// <summary>
    /// Decodes a 15-byte NetBIOS workstation name (ASCII, space-padded) into
    /// a string with trailing spaces stripped. Returns
    /// <see langword="false"/> if the name contains non-printable bytes that
    /// suggest corruption.
    /// </summary>
    private static bool TryDecodeName(ReadOnlySpan<byte> nameBytes, out string? name) {
        name = null;
        // Find the first trailing-space run.
        var endExclusive = nameBytes.Length;
        while (endExclusive > 0 && nameBytes[endExclusive - 1] == ' ') endExclusive--;
        if (endExclusive == 0) return false;  // all-spaces name

        for (var i = 0; i < endExclusive; i++) {
            var b = nameBytes[i];
            if (b < 0x20 || b > 0x7E) return false;  // non-printable ASCII → corrupt
        }

        name = Encoding.ASCII.GetString(nameBytes[..endExclusive]);
        return true;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);
}
