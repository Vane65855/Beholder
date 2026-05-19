using Beholder.Core.Discovery;

namespace Beholder.Tests;

public sealed class MdnsServiceDiscoveryPacketBuilderTests {
    [Fact]
    public void BuildServiceTypeQuery_AirplayService_ProducesValidHeader() {
        var packet = MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery("_airplay._tcp.local", 0xABCD);

        Assert.Equal(0xAB, packet[0]);
        Assert.Equal(0xCD, packet[1]);
        Assert.Equal(0x00, packet[2]);  // flags hi (standard query)
        Assert.Equal(0x00, packet[3]);  // flags lo
        Assert.Equal(0x00, packet[4]);  // QDCOUNT hi
        Assert.Equal(0x01, packet[5]);  // QDCOUNT lo
        Assert.Equal(0x00, packet[6]);  // ANCOUNT
        Assert.Equal(0x00, packet[7]);
        Assert.Equal(0x00, packet[8]);  // NSCOUNT
        Assert.Equal(0x00, packet[9]);
        Assert.Equal(0x00, packet[10]); // ARCOUNT
        Assert.Equal(0x00, packet[11]);
    }

    [Fact]
    public void BuildServiceTypeQuery_QnameLabelsMatchServiceType() {
        var packet = MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery("_airplay._tcp.local", 0x1234);

        // QNAME starts at offset 12 (after the 12-byte header).
        // Expect labels: "_airplay" (8), "_tcp" (4), "local" (5), root terminator (0).
        var p = 12;
        Assert.Equal(8, packet[p]);
        Assert.Equal((byte)'_', packet[p + 1]); Assert.Equal((byte)'a', packet[p + 2]);
        Assert.Equal((byte)'i', packet[p + 3]); Assert.Equal((byte)'r', packet[p + 4]);
        Assert.Equal((byte)'p', packet[p + 5]); Assert.Equal((byte)'l', packet[p + 6]);
        Assert.Equal((byte)'a', packet[p + 7]); Assert.Equal((byte)'y', packet[p + 8]);
        p += 9;

        Assert.Equal(4, packet[p]);
        Assert.Equal((byte)'_', packet[p + 1]); Assert.Equal((byte)'t', packet[p + 2]);
        Assert.Equal((byte)'c', packet[p + 3]); Assert.Equal((byte)'p', packet[p + 4]);
        p += 5;

        Assert.Equal(5, packet[p]);
        Assert.Equal((byte)'l', packet[p + 1]); Assert.Equal((byte)'o', packet[p + 2]);
        Assert.Equal((byte)'c', packet[p + 3]); Assert.Equal((byte)'a', packet[p + 4]);
        Assert.Equal((byte)'l', packet[p + 5]);
        p += 6;

        Assert.Equal(0, packet[p]);  // root terminator
    }

    [Fact]
    public void BuildServiceTypeQuery_QClassHasQuBitSet() {
        var packet = MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery("_airplay._tcp.local", 0x0001);

        // QTYPE + QCLASS are the last 4 bytes. QCLASS high bit set = 0x8001.
        var qclass = (packet[^2] << 8) | packet[^1];
        Assert.Equal(0x8001, qclass);
    }

    [Fact]
    public void BuildServiceTypeQuery_QTypeIsPtr() {
        var packet = MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery("_airplay._tcp.local", 0x0001);

        var qtype = (packet[^4] << 8) | packet[^3];
        Assert.Equal(0x000C, qtype);  // PTR record type
    }

    [Fact]
    public void BuildServiceTypeQuery_MultipleCallsSameInputDifferentTid_DifferentPackets() {
        var packetA = MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery("_airplay._tcp.local", 0x1111);
        var packetB = MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery("_airplay._tcp.local", 0x2222);

        Assert.Equal(packetA.Length, packetB.Length);
        Assert.NotEqual(packetA[0], packetB[0]);  // TID differs
        Assert.NotEqual(packetA[1], packetB[1]);
        // Everything after the TID should match.
        for (var i = 2; i < packetA.Length; i++) {
            Assert.Equal(packetA[i], packetB[i]);
        }
    }

    [Fact]
    public void BuildServiceTypeQuery_UdpProtocol_AcceptedAndEncodedCorrectly() {
        // Service-type names can use _udp instead of _tcp.
        var packet = MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery("_ssh._udp.local", 0x0001);

        // QNAME at offset 12 — expect "_ssh" (4), "_udp" (4), "local" (5), terminator (0).
        Assert.Equal(4, packet[12]);
        Assert.Equal((byte)'_', packet[13]); Assert.Equal((byte)'s', packet[14]);
        Assert.Equal((byte)'s', packet[15]); Assert.Equal((byte)'h', packet[16]);
        Assert.Equal(4, packet[17]);
        Assert.Equal((byte)'_', packet[18]); Assert.Equal((byte)'u', packet[19]);
        Assert.Equal((byte)'d', packet[20]); Assert.Equal((byte)'p', packet[21]);
    }

    [Fact]
    public void BuildServiceTypeQuery_NullServiceType_ThrowsArgumentNullException() {
        // ArgumentException.ThrowIfNullOrWhiteSpace throws the derived
        // ArgumentNullException for null specifically — xUnit's Assert.Throws
        // requires an exact match, so we check for the derived type.
        Assert.Throws<ArgumentNullException>(
            () => MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery(null!, 0x0001));
    }

    [Fact]
    public void BuildServiceTypeQuery_EmptyServiceType_ThrowsArgumentException() {
        Assert.Throws<ArgumentException>(
            () => MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery("", 0x0001));
    }

    [Fact]
    public void BuildServiceTypeQuery_WhitespaceServiceType_ThrowsArgumentException() {
        Assert.Throws<ArgumentException>(
            () => MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery("   ", 0x0001));
    }

    [Fact]
    public void BuildServiceTypeQuery_MissingProtoLabel_ThrowsArgumentException() {
        // Only two labels — must reject.
        Assert.Throws<ArgumentException>(
            () => MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery("_airplay.local", 0x0001));
    }

    [Fact]
    public void BuildServiceTypeQuery_MissingLocalSuffix_ThrowsArgumentException() {
        Assert.Throws<ArgumentException>(
            () => MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery("_airplay._tcp.example", 0x0001));
    }

    [Fact]
    public void BuildServiceTypeQuery_InvalidProtocol_ThrowsArgumentException() {
        // Only _tcp / _udp are valid DNS-SD protocols.
        Assert.Throws<ArgumentException>(
            () => MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery("_airplay._sctp.local", 0x0001));
    }

    [Fact]
    public void BuildServiceTypeQuery_ServiceLabelMissingUnderscore_ThrowsArgumentException() {
        Assert.Throws<ArgumentException>(
            () => MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery("airplay._tcp.local", 0x0001));
    }

    [Fact]
    public void BuildServiceTypeQuery_LabelExceedsMaxLength_ThrowsArgumentException() {
        // DNS labels max out at 63 bytes (RFC 1035 §2.3.4).
        var longService = "_" + new string('a', 63) + "._tcp.local";
        Assert.Throws<ArgumentException>(
            () => MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery(longService, 0x0001));
    }

    [Fact]
    public void BuildServiceTypeQuery_TotalLengthMatchesEncodedSize() {
        // Header (12) + qname (1+8 + 1+4 + 1+5 + 1) + QTYPE (2) + QCLASS (2) = 12 + 21 + 4 = 37.
        var packet = MdnsServiceDiscoveryPacketBuilder.BuildServiceTypeQuery("_airplay._tcp.local", 0x0001);
        Assert.Equal(37, packet.Length);
    }
}
