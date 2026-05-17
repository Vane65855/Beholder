using Beholder.Core.Discovery;

namespace Beholder.Tests;

public sealed class NetbiosPacketParserTests {
    // --- Builder ---

    [Fact]
    public void BuildNbstatQuery_Produces50ByteFixedLengthPacket() {
        var packet = NetbiosPacketBuilder.BuildNbstatQuery(0xABCD);

        Assert.Equal(50, packet.Length);
    }

    [Fact]
    public void BuildNbstatQuery_HeaderEncodesTidAndQueryFlags() {
        var packet = NetbiosPacketBuilder.BuildNbstatQuery(0xABCD);

        Assert.Equal(0xAB, packet[0]);
        Assert.Equal(0xCD, packet[1]);
        Assert.Equal(0x00, packet[2]);            // Flags hi (standard query)
        Assert.Equal(0x00, packet[3]);            // Flags lo
        Assert.Equal(0x00, packet[4]); Assert.Equal(0x01, packet[5]);  // QDCOUNT = 1
        Assert.Equal(0x00, packet[6]); Assert.Equal(0x00, packet[7]);  // ANCOUNT
    }

    [Fact]
    public void BuildNbstatQuery_QnameUsesWildcardFirstLevelEncoding() {
        var packet = NetbiosPacketBuilder.BuildNbstatQuery(0xABCD);

        // Byte 12 = length prefix (0x20 = 32 chars to follow).
        Assert.Equal(0x20, packet[12]);

        // Bytes 13-44 = first-level-encoded wildcard "*\0\0...\0":
        // '*' = 0x2A → high nibble 2 = 'C', low nibble A = 'K' → "CK"
        // 0x00 = 'A' 'A' → 15 × "AA" = 30 chars
        Assert.Equal((byte)'C', packet[13]);
        Assert.Equal((byte)'K', packet[14]);
        for (var i = 0; i < 15; i++) {
            Assert.Equal((byte)'A', packet[15 + (i * 2)]);
            Assert.Equal((byte)'A', packet[15 + (i * 2) + 1]);
        }

        // Byte 45 = name terminator (0x00).
        Assert.Equal(0x00, packet[45]);
    }

    [Fact]
    public void BuildNbstatQuery_TypeAndClassAreNbstatAndIn() {
        var packet = NetbiosPacketBuilder.BuildNbstatQuery(0xABCD);

        // QTYPE = NBSTAT (0x0021) at bytes 46-47.
        Assert.Equal(0x00, packet[46]);
        Assert.Equal(0x21, packet[47]);
        // QCLASS = IN (0x0001) at bytes 48-49.
        Assert.Equal(0x00, packet[48]);
        Assert.Equal(0x01, packet[49]);
    }

    // --- Parser ---

    [Fact]
    public void TryExtractHostname_ValidResponseWithWorkstationName_ReturnsName() {
        var response = BuildNbstatResponse(0xABCD, [
            ("VANE-PC", 0x00, IsGroup: false),
            ("WORKGROUP", 0x00, IsGroup: true),  // group: skipped
        ]);

        var success = NetbiosPacketParser.TryExtractHostname(response, 0xABCD, out var hostname);

        Assert.True(success);
        Assert.Equal("VANE-PC", hostname);
    }

    [Fact]
    public void TryExtractHostname_WorkstationNamePaddedWithSpaces_StripsTrailing() {
        var response = BuildNbstatResponse(0xABCD, [
            ("SHORT-NAME    ", 0x00, IsGroup: false),  // padded to <=15 chars by helper
        ]);

        var success = NetbiosPacketParser.TryExtractHostname(response, 0xABCD, out var hostname);

        Assert.True(success);
        Assert.Equal("SHORT-NAME", hostname);
    }

    [Fact]
    public void TryExtractHostname_OnlyGroupNames_ReturnsFalse() {
        var response = BuildNbstatResponse(0xABCD, [
            ("WORKGROUP", 0x00, IsGroup: true),
        ]);

        var success = NetbiosPacketParser.TryExtractHostname(response, 0xABCD, out var hostname);

        Assert.False(success);
        Assert.Null(hostname);
    }

    [Fact]
    public void TryExtractHostname_GroupFirstUniqueSecond_FindsUnique() {
        var response = BuildNbstatResponse(0xABCD, [
            ("WORKGROUP", 0x00, IsGroup: true),
            ("VANE-PC", 0x00, IsGroup: false),
        ]);

        var success = NetbiosPacketParser.TryExtractHostname(response, 0xABCD, out var hostname);

        Assert.True(success);
        Assert.Equal("VANE-PC", hostname);
    }

    [Fact]
    public void TryExtractHostname_NonWorkstationSuffix_Skipped() {
        var response = BuildNbstatResponse(0xABCD, [
            ("DOMAIN-CONTROLLER", 0x1C, IsGroup: false),  // domain controller suffix, not workstation
        ]);

        var success = NetbiosPacketParser.TryExtractHostname(response, 0xABCD, out var hostname);

        Assert.False(success);
        Assert.Null(hostname);
    }

    [Fact]
    public void TryExtractHostname_WrongTransactionId_ReturnsFalse() {
        var response = BuildNbstatResponse(0xABCD, [
            ("VANE-PC", 0x00, IsGroup: false),
        ]);

        var success = NetbiosPacketParser.TryExtractHostname(response, 0x1234, out var hostname);

        Assert.False(success);
        Assert.Null(hostname);
    }

    [Fact]
    public void TryExtractHostname_TruncatedResponse_ReturnsFalse() {
        Span<byte> truncated = stackalloc byte[6];
        truncated[0] = 0xAB; truncated[1] = 0xCD;

        var success = NetbiosPacketParser.TryExtractHostname(truncated, 0xABCD, out var hostname);

        Assert.False(success);
        Assert.Null(hostname);
    }

    [Fact]
    public void TryExtractHostname_EmptyBuffer_ReturnsFalse() {
        Span<byte> empty = [];

        var success = NetbiosPacketParser.TryExtractHostname(empty, 0xABCD, out var hostname);

        Assert.False(success);
        Assert.Null(hostname);
    }

    [Fact]
    public void TryExtractHostname_NoAnswers_ReturnsFalse() {
        // Header with QDCOUNT=1 but ANCOUNT=0, then a question.
        var header = new byte[12];
        header[0] = 0xAB; header[1] = 0xCD;
        header[4] = 0x00; header[5] = 0x01;  // QDCOUNT
        header[6] = 0x00; header[7] = 0x00;  // ANCOUNT = 0

        var question = BuildNbstatQuestionSection();
        var response = new byte[header.Length + question.Length];
        Array.Copy(header, response, header.Length);
        Array.Copy(question, 0, response, header.Length, question.Length);

        var success = NetbiosPacketParser.TryExtractHostname(response, 0xABCD, out var hostname);

        Assert.False(success);
        Assert.Null(hostname);
    }

    // --- Test helpers ---

    /// <summary>
    /// Builds an NBSTAT response packet with a header, a single question
    /// section (the QU echoed back), and a single answer containing the
    /// list of NetBIOS names.
    /// </summary>
    private static byte[] BuildNbstatResponse(
        ushort transactionId,
        (string Name, byte Suffix, bool IsGroup)[] names
    ) {
        var header = new byte[12];
        header[0] = (byte)(transactionId >> 8);
        header[1] = (byte)transactionId;
        header[2] = 0x84; header[3] = 0x00;  // Flags = QR + AA (response, authoritative)
        header[4] = 0x00; header[5] = 0x01;  // QDCOUNT
        header[6] = 0x00; header[7] = 0x01;  // ANCOUNT

        var question = BuildNbstatQuestionSection();

        // RDATA: NUM_NAMES (1) + names (18 each) + node stats (46).
        var rdataLen = 1 + (names.Length * 18) + 46;
        var rdata = new byte[rdataLen];
        rdata[0] = (byte)names.Length;
        for (var i = 0; i < names.Length; i++) {
            var (nm, suffix, isGroup) = names[i];
            var offset = 1 + (i * 18);
            // 15-byte ASCII name, space-padded.
            for (var j = 0; j < 15; j++) {
                rdata[offset + j] = j < nm.Length ? (byte)nm[j] : (byte)' ';
            }
            rdata[offset + 15] = suffix;
            // Flags: high byte's high bit = G_NAME bit (set for group).
            ushort flags = isGroup ? (ushort)0x8000 : (ushort)0x0400;  // 0x0400 = active name
            rdata[offset + 16] = (byte)(flags >> 8);
            rdata[offset + 17] = (byte)flags;
        }
        // Node stats: 46 zero bytes, fine for parsing.

        // Answer header: NAME (compression ptr to question = 0x0C) + TYPE + CLASS + TTL + RDLENGTH.
        var answer = new byte[12 + rdataLen];
        answer[0] = 0xC0; answer[1] = 0x0C;
        answer[2] = 0x00; answer[3] = 0x21;     // TYPE = NBSTAT
        answer[4] = 0x00; answer[5] = 0x01;     // CLASS = IN
        answer[6] = 0x00; answer[7] = 0x00;
        answer[8] = 0x00; answer[9] = 0x3C;     // TTL = 60
        answer[10] = (byte)(rdataLen >> 8); answer[11] = (byte)rdataLen;
        Array.Copy(rdata, 0, answer, 12, rdataLen);

        var total = new byte[header.Length + question.Length + answer.Length];
        Array.Copy(header, total, header.Length);
        Array.Copy(question, 0, total, header.Length, question.Length);
        Array.Copy(answer, 0, total, header.Length + question.Length, answer.Length);
        return total;
    }

    /// <summary>
    /// Builds the question section of an NBSTAT response — the QU echoed
    /// back: 1-byte length (0x20) + 32-byte first-level-encoded wildcard
    /// name + 1-byte terminator + 2-byte QTYPE + 2-byte QCLASS = 38 bytes.
    /// </summary>
    private static byte[] BuildNbstatQuestionSection() {
        var q = new byte[38];
        q[0] = 0x20;
        // Wildcard "CKAAAAA...A" (32 chars).
        q[1] = (byte)'C'; q[2] = (byte)'K';
        for (var i = 0; i < 15; i++) {
            q[3 + (i * 2)] = (byte)'A';
            q[3 + (i * 2) + 1] = (byte)'A';
        }
        q[33] = 0x00;        // terminator
        q[34] = 0x00; q[35] = 0x21;  // QTYPE = NBSTAT
        q[36] = 0x00; q[37] = 0x01;  // QCLASS = IN
        return q;
    }
}
