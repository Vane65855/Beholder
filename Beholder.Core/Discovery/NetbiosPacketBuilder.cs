namespace Beholder.Core.Discovery;

/// <summary>
/// Builds RFC 1002 NetBIOS Node Status (NBSTAT) query packets. The Phase
/// 9.2.5 scanner sends one NBSTAT query per LAN device to retrieve its
/// registered NetBIOS names; the parser extracts the workstation name (the
/// first unique name with suffix byte <c>0x00</c>).
/// </summary>
/// <remarks>
/// NetBIOS "first-level encoding" (RFC 1001 §14.1) is the unusual part:
/// every 16-byte NetBIOS name is encoded as 32 ASCII letters where each
/// nibble of each byte maps to a letter (high nibble first, both in the
/// range <c>A..P</c>). The query uses the wildcard name <c>"*"</c>
/// (0x2A followed by 15 zero bytes), which encodes to
/// <c>"CKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"</c> — the NBSTAT idiom for
/// "node status, any responder."
/// </remarks>
public static class NetbiosPacketBuilder {
    /// <summary>NetBIOS Node Status Request, equivalent to DNS QTYPE.</summary>
    public const ushort NbstatQueryType = 0x0021;

    /// <summary>NetBIOS Internet class, equivalent to DNS QCLASS.</summary>
    public const ushort NbstatQueryClass = 0x0001;

    private const int NbHeaderLength = 12;
    private const int EncodedNameLength = 32;
    private const byte FirstLevelEncodingBase = (byte)'A';

    /// <summary>
    /// Builds an NBSTAT query for the wildcard NetBIOS name <c>"*"</c>.
    /// The packet is 50 bytes total: 12-byte header + 1-byte name-length
    /// prefix + 32-byte encoded wildcard name + 1-byte name terminator +
    /// 2-byte QTYPE + 2-byte QCLASS.
    /// </summary>
    public static byte[] BuildNbstatQuery(ushort transactionId) {
        // 12 (header) + 1 (label len) + 32 (encoded) + 1 (terminator) + 4 (QTYPE+QCLASS)
        var packet = new byte[50];

        // Header.
        WriteU16(packet, 0, transactionId);
        WriteU16(packet, 2, 0x0000);  // Flags = standard query
        WriteU16(packet, 4, 0x0001);  // QDCOUNT
        WriteU16(packet, 6, 0x0000);  // ANCOUNT
        WriteU16(packet, 8, 0x0000);  // NSCOUNT
        WriteU16(packet, 10, 0x0000); // ARCOUNT

        // QNAME: 0x20 length + 32 encoded chars + 0x00 terminator.
        packet[12] = EncodedNameLength;

        // Raw wildcard NetBIOS name: '*' + 15 zero bytes.
        Span<byte> rawName = stackalloc byte[16];
        rawName[0] = (byte)'*';
        // rawName[1..16] already zero from stackalloc.

        // First-level encoding: each byte → two chars, each char = 'A' + nibble.
        for (var i = 0; i < 16; i++) {
            var b = rawName[i];
            packet[13 + (i * 2)] = (byte)(FirstLevelEncodingBase + ((b >> 4) & 0x0F));
            packet[13 + (i * 2) + 1] = (byte)(FirstLevelEncodingBase + (b & 0x0F));
        }

        packet[45] = 0x00;  // root-label terminator

        WriteU16(packet, 46, NbstatQueryType);
        WriteU16(packet, 48, NbstatQueryClass);

        return packet;
    }

    private static void WriteU16(byte[] data, int offset, ushort value) {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }
}
