using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Beholder.Core.Discovery;

/// <summary>
/// Builds RFC 6762 mDNS query packets. Per the Phase 9.2.5 design, the
/// scanner sends a PTR query for the reverse-IP arpa name of each LAN
/// device, with the QU bit (unicast response wanted) set per RFC 6762 §5.4
/// so responders unicast the reply back to the sender's ephemeral source
/// port instead of multicasting it to 5353.
/// </summary>
/// <remarks>
/// Pure static; no socket I/O, no dependencies beyond <see cref="IPAddress"/>
/// and primitives. Unit-tested against hand-built RFC examples.
/// </remarks>
public static class MdnsPacketBuilder {
    private const ushort QueryTypePtr = 0x000C;     // DNS PTR record type
    private const ushort QueryClassIn = 0x0001;     // DNS IN class
    private const ushort QueryClassQuBit = 0x8000;  // RFC 6762 §5.4: unicast response wanted
    private const int DnsHeaderLength = 12;
    private const int InAddrArpaSuffixLength = 14;  // "\x07in-addr\x04arpa\x00"

    /// <summary>
    /// Builds a PTR query for the reverse-IP arpa name of
    /// <paramref name="ip"/>. For example, <c>192.168.1.42</c> becomes a
    /// query for <c>42.1.168.192.in-addr.arpa</c>. QCLASS has the QU bit
    /// set so responders unicast the reply to the source ephemeral port.
    /// Throws <see cref="ArgumentException"/> for non-IPv4 addresses.
    /// </summary>
    public static byte[] BuildPtrQuery(IPAddress ip, ushort transactionId) {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.AddressFamily != AddressFamily.InterNetwork) {
            throw new ArgumentException("mDNS reverse-IP query requires IPv4", nameof(ip));
        }

        var ipBytes = ip.GetAddressBytes();

        // Reversed-octet labels: e.g. "42", "1", "168", "192".
        // Each label = 1 length byte + ASCII chars. Max label length is 3 (e.g. "255").
        Span<byte> octetLabels = stackalloc byte[16];  // 4 labels × max 4 bytes each
        var labelCursor = 0;
        for (var i = 3; i >= 0; i--) {
            var octetStr = ipBytes[i].ToString();
            octetLabels[labelCursor++] = (byte)octetStr.Length;
            foreach (var ch in octetStr) octetLabels[labelCursor++] = (byte)ch;
        }

        // QNAME: reversed octets + "in-addr.arpa" suffix encoded as DNS labels.
        // Suffix bytes: 0x07 'i' 'n' '-' 'a' 'd' 'd' 'r' 0x04 'a' 'r' 'p' 'a' 0x00
        var qnameLength = labelCursor + InAddrArpaSuffixLength;
        var totalLength = DnsHeaderLength + qnameLength + 4;  // +4 = QTYPE + QCLASS
        var packet = new byte[totalLength];

        // Header.
        WriteU16(packet, 0, transactionId);
        WriteU16(packet, 2, 0x0000);  // Flags = standard query, not authoritative, not recursive
        WriteU16(packet, 4, 0x0001);  // QDCOUNT = 1
        WriteU16(packet, 6, 0x0000);  // ANCOUNT
        WriteU16(packet, 8, 0x0000);  // NSCOUNT
        WriteU16(packet, 10, 0x0000); // ARCOUNT

        // QNAME.
        var qnameOffset = DnsHeaderLength;
        octetLabels[..labelCursor].CopyTo(packet.AsSpan(qnameOffset));
        WriteInAddrArpaSuffix(packet, qnameOffset + labelCursor);

        // QTYPE + QCLASS (with QU bit).
        WriteU16(packet, qnameOffset + qnameLength, QueryTypePtr);
        WriteU16(packet, qnameOffset + qnameLength + 2, QueryClassIn | QueryClassQuBit);

        return packet;
    }

    /// <summary>
    /// Writes the literal byte sequence for <c>"\x07in-addr\x04arpa\x00"</c>
    /// — the DNS-label-encoded <c>in-addr.arpa</c> suffix shared by every
    /// reverse-IPv4 PTR query. Extracted so the calling builder stays
    /// readable.
    /// </summary>
    private static void WriteInAddrArpaSuffix(byte[] packet, int offset) {
        // Label "in-addr" (length 7).
        packet[offset++] = 7;
        var inAddr = "in-addr"u8;
        inAddr.CopyTo(packet.AsSpan(offset));
        offset += inAddr.Length;

        // Label "arpa" (length 4).
        packet[offset++] = 4;
        var arpa = "arpa"u8;
        arpa.CopyTo(packet.AsSpan(offset));
        offset += arpa.Length;

        // Root-label terminator.
        packet[offset] = 0x00;
    }

    private static void WriteU16(byte[] data, int offset, ushort value) {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }
}
