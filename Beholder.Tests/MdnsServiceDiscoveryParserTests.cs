using Beholder.Core.Discovery;

namespace Beholder.Tests;

public sealed class MdnsServiceDiscoveryParserTests {
    private static readonly IReadOnlySet<ushort> SingleTid = new HashSet<ushort> { 0xABCD };

    // --- Happy paths ---

    [Fact]
    public void TryExtractHostname_ValidResponseWithPtrSrvAndA_ReturnsSrvTarget() {
        // PTR + SRV + A — SRV target wins. SRV target = "living-room-tv.local".
        var packet = new MdnsResponseBuilder(0xABCD)
            .WithQuestion("_airplay._tcp.local")
            .AddAnswer(BuildPtrRecord(ownerName: "_airplay._tcp.local", target: "LivingRoom._airplay._tcp.local"))
            .AddAdditional(BuildSrvRecord(ownerName: "LivingRoom._airplay._tcp.local", priority: 0, weight: 0, port: 7000, target: "living-room-tv.local"))
            .AddAdditional(BuildARecord(ownerName: "living-room-tv.local", ip: [192, 168, 1, 42]))
            .Build();

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(packet, SingleTid, out var hostname);

        Assert.True(success);
        Assert.Equal("living-room-tv", hostname);
    }

    [Fact]
    public void TryExtractHostname_PtrAndSrv_PrefersSrvTarget() {
        // SRV target wins over PTR instance label.
        var packet = new MdnsResponseBuilder(0xABCD)
            .WithQuestion("_airplay._tcp.local")
            .AddAnswer(BuildPtrRecord(ownerName: "_airplay._tcp.local", target: "FriendlyInstance._airplay._tcp.local"))
            .AddAdditional(BuildSrvRecord(ownerName: "FriendlyInstance._airplay._tcp.local", priority: 0, weight: 0, port: 7000, target: "apple-tv.local"))
            .Build();

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(packet, SingleTid, out var hostname);

        Assert.True(success);
        Assert.Equal("apple-tv", hostname);
    }

    [Fact]
    public void TryExtractHostname_PtrAndAOnly_FallsBackToARecordOwner() {
        // No SRV — A record's owner name is the hostname.
        var packet = new MdnsResponseBuilder(0xABCD)
            .WithQuestion("_workstation._tcp.local")
            .AddAnswer(BuildPtrRecord(ownerName: "_workstation._tcp.local", target: "myhost._workstation._tcp.local"))
            .AddAdditional(BuildARecord(ownerName: "myhost.local", ip: [10, 0, 0, 5]))
            .Build();

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(packet, SingleTid, out var hostname);

        Assert.True(success);
        Assert.Equal("myhost", hostname);
    }

    [Fact]
    public void TryExtractHostname_PtrOnly_FallsBackToInstanceLeftmostLabel() {
        // Only PTR record — fall back to its instance leftmost label.
        var packet = new MdnsResponseBuilder(0xABCD)
            .WithQuestion("_airplay._tcp.local")
            .AddAnswer(BuildPtrRecord(ownerName: "_airplay._tcp.local", target: "kitchen-speaker._airplay._tcp.local"))
            .Build();

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(packet, SingleTid, out var hostname);

        Assert.True(success);
        Assert.Equal("kitchen-speaker", hostname);
    }

    [Fact]
    public void TryExtractHostname_PtrInstanceWithSpaces_AcceptedAsPrintableAscii() {
        // Real-world DNS-SD instances often have spaces in the leftmost label
        // (e.g., "Living Room TV"). Our TryReadFirstLabel helper uses the
        // loose "printable ASCII" rule so this case works.
        var packet = new MdnsResponseBuilder(0xABCD)
            .WithQuestion("_airplay._tcp.local")
            .AddAnswer(BuildPtrRecordWithRawInstance(
                ownerName: "_airplay._tcp.local",
                instanceLabelBytes: System.Text.Encoding.ASCII.GetBytes("Living Room TV"),
                remainder: "_airplay._tcp.local"))
            .Build();

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(packet, SingleTid, out var hostname);

        Assert.True(success);
        Assert.Equal("Living Room TV", hostname);
    }

    [Fact]
    public void TryExtractHostname_StripsTrailingLocalDot() {
        // SRV target "iphone.local" — `.local` suffix should be stripped.
        var packet = new MdnsResponseBuilder(0xABCD)
            .WithQuestion("_airplay._tcp.local")
            .AddAnswer(BuildPtrRecord(ownerName: "_airplay._tcp.local", target: "Instance._airplay._tcp.local"))
            .AddAdditional(BuildSrvRecord(ownerName: "Instance._airplay._tcp.local", priority: 0, weight: 0, port: 7000, target: "iphone.local"))
            .Build();

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(packet, SingleTid, out var hostname);

        Assert.True(success);
        Assert.Equal("iphone", hostname);
    }

    [Fact]
    public void TryExtractHostname_SrvTargetWithoutLocalSuffix_NotStrippedOfOtherLabels() {
        // Defensive: a SRV target like "host.example.com" (unusual on mDNS but
        // shouldn't crash) — only `.local` gets stripped, nothing else.
        var packet = new MdnsResponseBuilder(0xABCD)
            .WithQuestion("_airplay._tcp.local")
            .AddAnswer(BuildPtrRecord(ownerName: "_airplay._tcp.local", target: "X._airplay._tcp.local"))
            .AddAdditional(BuildSrvRecord(ownerName: "X._airplay._tcp.local", priority: 0, weight: 0, port: 7000, target: "host.example.com"))
            .Build();

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(packet, SingleTid, out var hostname);

        Assert.True(success);
        Assert.Equal("host.example.com", hostname);
    }

    [Fact]
    public void TryExtractHostname_AnyMatchingTid_Accepted() {
        // We send multiple queries from one socket (one TID per service-type).
        // The parser's expected-TID set holds all of them; any match accepts.
        var expectedTids = new HashSet<ushort> { 0x1111, 0x2222, 0x3333 };

        var packet = new MdnsResponseBuilder(0x2222)
            .WithQuestion("_airplay._tcp.local")
            .AddAnswer(BuildPtrRecord(ownerName: "_airplay._tcp.local", target: "device._airplay._tcp.local"))
            .Build();

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(packet, expectedTids, out var hostname);

        Assert.True(success);
        Assert.Equal("device", hostname);
    }

    // --- Defensive rejection paths ---

    [Fact]
    public void TryExtractHostname_WrongTransactionId_ReturnsFalse() {
        var packet = new MdnsResponseBuilder(0x9999)
            .WithQuestion("_airplay._tcp.local")
            .AddAnswer(BuildPtrRecord(ownerName: "_airplay._tcp.local", target: "device._airplay._tcp.local"))
            .Build();

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(packet, SingleTid, out var hostname);

        Assert.False(success);
        Assert.Null(hostname);
    }

    [Fact]
    public void TryExtractHostname_EmptyBuffer_ReturnsFalse() {
        Span<byte> empty = [];

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(empty, SingleTid, out var hostname);

        Assert.False(success);
        Assert.Null(hostname);
    }

    [Fact]
    public void TryExtractHostname_TruncatedHeader_ReturnsFalse() {
        Span<byte> truncated = stackalloc byte[6];
        truncated[0] = 0xAB; truncated[1] = 0xCD;

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(truncated, SingleTid, out var hostname);

        Assert.False(success);
        Assert.Null(hostname);
    }

    [Fact]
    public void TryExtractHostname_NoRecords_ReturnsFalse() {
        // 12-byte header with all counts = 0.
        Span<byte> headerOnly = stackalloc byte[12];
        headerOnly[0] = 0xAB; headerOnly[1] = 0xCD;
        // Counts all zero.

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(headerOnly, SingleTid, out var hostname);

        Assert.False(success);
        Assert.Null(hostname);
    }

    [Fact]
    public void TryExtractHostname_NonExtractableRecords_ReturnsFalse() {
        // Build a response with only a TXT record (type 0x0010) — no PTR/SRV/A.
        // Parser should skip and ultimately return false.
        var packet = new MdnsResponseBuilder(0xABCD)
            .WithQuestion("_airplay._tcp.local")
            .AddAnswer(BuildTxtRecord(ownerName: "Instance._airplay._tcp.local", text: "key=value"))
            .Build();

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(packet, SingleTid, out var hostname);

        Assert.False(success);
        Assert.Null(hostname);
    }

    [Fact]
    public void TryExtractHostname_NullExpectedSet_ThrowsArgumentNullException() {
        var packetBytes = new byte[12];
        Assert.Throws<ArgumentNullException>(
            () => MdnsServiceDiscoveryParser.TryExtractHostname(packetBytes, null!, out _));
    }

    [Fact]
    public void TryExtractHostname_RdLengthOverflowsBuffer_ReturnsFalse() {
        // Build a record whose RDLENGTH claims more bytes than remain in the
        // buffer — parser must bounds-check and return false.
        var qname = EncodeName("_airplay._tcp.local");
        var totalLength = 12 + qname.Length + 4 + 2 + 10;  // header + question + ptr-to-q name + fixed fields
        var packet = new byte[totalLength];

        // Header.
        packet[0] = 0xAB; packet[1] = 0xCD;
        packet[4] = 0x00; packet[5] = 0x01;  // QDCOUNT
        packet[6] = 0x00; packet[7] = 0x01;  // ANCOUNT

        // Question.
        qname.CopyTo(packet, 12);
        packet[12 + qname.Length + 0] = 0x00; packet[12 + qname.Length + 1] = 0x0C;  // QTYPE PTR
        packet[12 + qname.Length + 2] = 0x00; packet[12 + qname.Length + 3] = 0x01;  // QCLASS IN

        // Answer: name (compression pointer to 0x000C = question's qname),
        // TYPE PTR, CLASS IN, TTL 60, RDLENGTH = 0xFFFF (huge — overflows).
        var ans = 12 + qname.Length + 4;
        packet[ans + 0] = 0xC0; packet[ans + 1] = 0x0C;
        packet[ans + 2] = 0x00; packet[ans + 3] = 0x0C;  // TYPE
        packet[ans + 4] = 0x00; packet[ans + 5] = 0x01;  // CLASS
        packet[ans + 6] = 0; packet[ans + 7] = 0; packet[ans + 8] = 0; packet[ans + 9] = 60;  // TTL
        packet[ans + 10] = 0xFF; packet[ans + 11] = 0xFF;  // RDLENGTH = 65535

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(packet, SingleTid, out var hostname);

        Assert.False(success);
        Assert.Null(hostname);
    }

    [Fact]
    public void TryExtractHostname_QuestionSectionTruncated_ReturnsFalse() {
        // Header claims QDCOUNT=1 + ANCOUNT=1 but the buffer doesn't actually
        // contain a complete question — must not read past the buffer.
        var packet = new byte[12 + 3];
        packet[0] = 0xAB; packet[1] = 0xCD;
        packet[4] = 0x00; packet[5] = 0x01;  // QDCOUNT
        packet[6] = 0x00; packet[7] = 0x01;  // ANCOUNT
        // Question: a single label of length 100 (claims to be 100 bytes long)
        // but the buffer only has 2 more bytes — parser must bounds-check.
        packet[12] = 100;
        packet[13] = (byte)'a';
        packet[14] = (byte)'b';

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(packet, SingleTid, out var hostname);

        Assert.False(success);
        Assert.Null(hostname);
    }

    [Fact]
    public void TryExtractHostname_DnsNameCompression_ResolvesPointer() {
        // SRV target encoded as a compression pointer back to a name earlier
        // in the packet — exercise the pointer-following path.
        // We use the response builder's question name as the target.

        // First emit a packet with the SRV target == the question's QNAME
        // (the parser walks the SRV target as a DNS name; we'll prove the
        // pointer-follow works by using compression on the SRV target).

        // Construct the response manually to control the SRV target's encoding.
        var qname = EncodeName("_airplay._tcp.local");
        var instanceName = EncodeName("Instance._airplay._tcp.local");
        // SRV record:
        //   NAME: compression pointer to instance offset
        //   TYPE: 0x0021 SRV
        //   CLASS: 0x0001 IN
        //   TTL: 60
        //   RDLENGTH: 2 + 2 + 2 + targetlen
        //   RDATA: priority(2) + weight(2) + port(2) + target

        // Target encoded as compression pointer to "_airplay._tcp.local" at offset 12.
        var srvTargetCompressed = new byte[] { 0xC0, 0x0C };  // pointer to offset 12 (qname)

        // Build:
        //   header (12)
        //   question: qname + 4
        //   answer (PTR): NAME=ptr-to-12, type=PTR, class=IN, ttl, rdlen, rdata=instanceName
        //   additional (SRV): NAME=ptr-to-instance-in-PTR-rdata, type=SRV, class=IN, ttl, rdlen, rdata=priority+weight+port+ptr-to-qname
        var headerLen = 12;
        var questionLen = qname.Length + 4;
        var ptrAnswerNameLen = 2;
        var ptrAnswerFixedLen = 10;
        var ptrAnswerRdata = instanceName;
        var ptrAnswerLen = ptrAnswerNameLen + ptrAnswerFixedLen + ptrAnswerRdata.Length;

        // SRV NAME also a pointer to where the instance starts inside the PTR record's RDATA.
        // PTR RDATA starts at: headerLen + questionLen + ptrAnswerNameLen + ptrAnswerFixedLen.
        var instanceOffsetInPacket = headerLen + questionLen + ptrAnswerNameLen + ptrAnswerFixedLen;
        var srvAnswerNameLen = 2;  // pointer to instanceOffsetInPacket
        var srvAnswerFixedLen = 10;
        var srvAnswerRdataLen = 6 + srvTargetCompressed.Length;  // priority + weight + port + target
        var srvAnswerLen = srvAnswerNameLen + srvAnswerFixedLen + srvAnswerRdataLen;

        var total = headerLen + questionLen + ptrAnswerLen + srvAnswerLen;
        var packet = new byte[total];

        // Header.
        packet[0] = 0xAB; packet[1] = 0xCD;
        packet[4] = 0x00; packet[5] = 0x01;  // QDCOUNT
        packet[6] = 0x00; packet[7] = 0x01;  // ANCOUNT
        packet[10] = 0x00; packet[11] = 0x01;  // ARCOUNT (SRV in additional)

        // Question.
        qname.CopyTo(packet, headerLen);
        packet[headerLen + qname.Length + 0] = 0x00; packet[headerLen + qname.Length + 1] = 0x0C;  // PTR
        packet[headerLen + qname.Length + 2] = 0x00; packet[headerLen + qname.Length + 3] = 0x01;  // IN

        // PTR answer.
        var ptrStart = headerLen + questionLen;
        packet[ptrStart + 0] = 0xC0; packet[ptrStart + 1] = 0x0C;  // name ptr → qname
        packet[ptrStart + 2] = 0x00; packet[ptrStart + 3] = 0x0C;  // type PTR
        packet[ptrStart + 4] = 0x00; packet[ptrStart + 5] = 0x01;  // class IN
        packet[ptrStart + 9] = 60;                                   // ttl
        packet[ptrStart + 10] = (byte)(ptrAnswerRdata.Length >> 8);
        packet[ptrStart + 11] = (byte)ptrAnswerRdata.Length;
        ptrAnswerRdata.CopyTo(packet, ptrStart + 12);

        // SRV additional.
        var srvStart = ptrStart + ptrAnswerLen;
        packet[srvStart + 0] = 0xC0; packet[srvStart + 1] = (byte)instanceOffsetInPacket;  // name ptr → instance
        packet[srvStart + 2] = 0x00; packet[srvStart + 3] = 0x21;  // type SRV
        packet[srvStart + 4] = 0x00; packet[srvStart + 5] = 0x01;  // class IN
        packet[srvStart + 9] = 60;                                   // ttl
        packet[srvStart + 10] = (byte)(srvAnswerRdataLen >> 8);
        packet[srvStart + 11] = (byte)srvAnswerRdataLen;
        // RDATA: priority + weight + port (all zero), then compressed target.
        packet[srvStart + 12] = 0; packet[srvStart + 13] = 0;     // priority
        packet[srvStart + 14] = 0; packet[srvStart + 15] = 0;     // weight
        packet[srvStart + 16] = 0; packet[srvStart + 17] = 80;    // port = 80
        packet[srvStart + 18] = 0xC0; packet[srvStart + 19] = 0x0C;  // target ptr → qname

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(packet, SingleTid, out var hostname);

        // SRV target resolved via compression = "_airplay._tcp.local" → strip ".local" → "_airplay._tcp".
        Assert.True(success);
        Assert.Equal("_airplay._tcp", hostname);
    }

    [Fact]
    public void TryExtractHostname_AdditionalSectionParsed_NotJustAnswers() {
        // Many real responses put the PTR in the answer section and SRV/A in
        // the additional section. We've already covered this implicitly in
        // earlier tests, but make it explicit: a response with NO answer
        // section and SRV+A only in additional should still resolve.
        var packet = new MdnsResponseBuilder(0xABCD)
            .WithQuestion("_airplay._tcp.local")
            .AddAdditional(BuildSrvRecord(ownerName: "Instance._airplay._tcp.local", priority: 0, weight: 0, port: 7000, target: "lonely-srv.local"))
            .Build();

        var success = MdnsServiceDiscoveryParser.TryExtractHostname(packet, SingleTid, out var hostname);

        Assert.True(success);
        Assert.Equal("lonely-srv", hostname);
    }

    // --- Test helpers ---

    /// <summary>
    /// Encodes a dotted DNS name as length-prefixed labels with a root
    /// terminator, e.g. "_airplay._tcp.local" →
    /// <c>[8]_airplay[4]_tcp[5]local[0]</c>.
    /// </summary>
    private static byte[] EncodeName(string name) {
        var labels = name.Split('.');
        var bytes = new List<byte>();
        foreach (var label in labels) {
            bytes.Add((byte)label.Length);
            foreach (var ch in label) bytes.Add((byte)ch);
        }
        bytes.Add(0x00);
        return bytes.ToArray();
    }

    private static byte[] BuildPtrRecord(string ownerName, string target) {
        var rdata = EncodeName(target);
        var rec = new List<byte>();
        rec.AddRange(EncodeName(ownerName));
        rec.AddRange([0x00, 0x0C]);                  // TYPE PTR
        rec.AddRange([0x00, 0x01]);                  // CLASS IN
        rec.AddRange([0x00, 0x00, 0x00, 0x3C]);      // TTL 60
        rec.AddRange([(byte)(rdata.Length >> 8), (byte)rdata.Length]);  // RDLENGTH
        rec.AddRange(rdata);
        return rec.ToArray();
    }

    /// <summary>
    /// PTR record with a raw instance label (not validated as DNS-safe) — used
    /// to test the printable-ASCII instance-label fallback path (e.g., spaces).
    /// </summary>
    private static byte[] BuildPtrRecordWithRawInstance(string ownerName, byte[] instanceLabelBytes, string remainder) {
        var remainderBytes = EncodeName(remainder);
        var rec = new List<byte>();
        rec.AddRange(EncodeName(ownerName));
        rec.AddRange([0x00, 0x0C]);                  // TYPE PTR
        rec.AddRange([0x00, 0x01]);                  // CLASS IN
        rec.AddRange([0x00, 0x00, 0x00, 0x3C]);      // TTL 60
        var rdataLen = 1 + instanceLabelBytes.Length + remainderBytes.Length;
        rec.AddRange([(byte)(rdataLen >> 8), (byte)rdataLen]);
        rec.Add((byte)instanceLabelBytes.Length);
        rec.AddRange(instanceLabelBytes);
        rec.AddRange(remainderBytes);
        return rec.ToArray();
    }

    private static byte[] BuildSrvRecord(string ownerName, ushort priority, ushort weight, ushort port, string target) {
        var targetBytes = EncodeName(target);
        var rdataLen = 6 + targetBytes.Length;
        var rec = new List<byte>();
        rec.AddRange(EncodeName(ownerName));
        rec.AddRange([0x00, 0x21]);                  // TYPE SRV
        rec.AddRange([0x00, 0x01]);                  // CLASS IN
        rec.AddRange([0x00, 0x00, 0x00, 0x3C]);      // TTL 60
        rec.AddRange([(byte)(rdataLen >> 8), (byte)rdataLen]);
        rec.AddRange([(byte)(priority >> 8), (byte)priority]);
        rec.AddRange([(byte)(weight >> 8), (byte)weight]);
        rec.AddRange([(byte)(port >> 8), (byte)port]);
        rec.AddRange(targetBytes);
        return rec.ToArray();
    }

    private static byte[] BuildARecord(string ownerName, byte[] ip) {
        var rec = new List<byte>();
        rec.AddRange(EncodeName(ownerName));
        rec.AddRange([0x00, 0x01]);                  // TYPE A
        rec.AddRange([0x00, 0x01]);                  // CLASS IN
        rec.AddRange([0x00, 0x00, 0x00, 0x3C]);      // TTL 60
        rec.AddRange([0x00, 0x04]);                  // RDLENGTH
        rec.AddRange(ip);
        return rec.ToArray();
    }

    private static byte[] BuildTxtRecord(string ownerName, string text) {
        var textBytes = System.Text.Encoding.ASCII.GetBytes(text);
        var rdataLen = 1 + textBytes.Length;
        var rec = new List<byte>();
        rec.AddRange(EncodeName(ownerName));
        rec.AddRange([0x00, 0x10]);                  // TYPE TXT
        rec.AddRange([0x00, 0x01]);                  // CLASS IN
        rec.AddRange([0x00, 0x00, 0x00, 0x3C]);      // TTL 60
        rec.AddRange([(byte)(rdataLen >> 8), (byte)rdataLen]);
        rec.Add((byte)textBytes.Length);
        rec.AddRange(textBytes);
        return rec.ToArray();
    }

    /// <summary>
    /// Fluent helper for assembling a multi-section mDNS response packet.
    /// </summary>
    private sealed class MdnsResponseBuilder {
        private readonly ushort _transactionId;
        private byte[]? _qname;
        private readonly List<byte[]> _answers = [];
        private readonly List<byte[]> _additionals = [];

        public MdnsResponseBuilder(ushort transactionId) {
            _transactionId = transactionId;
        }

        public MdnsResponseBuilder WithQuestion(string qname) {
            _qname = EncodeName(qname);
            return this;
        }

        public MdnsResponseBuilder AddAnswer(byte[] record) {
            _answers.Add(record);
            return this;
        }

        public MdnsResponseBuilder AddAdditional(byte[] record) {
            _additionals.Add(record);
            return this;
        }

        public byte[] Build() {
            var question = new List<byte>();
            if (_qname is not null) {
                question.AddRange(_qname);
                question.AddRange([0x00, 0x0C, 0x00, 0x01]);  // QTYPE PTR, QCLASS IN
            }

            var totalRecords = _answers.Sum(r => r.Length) + _additionals.Sum(r => r.Length);
            var packet = new byte[12 + question.Count + totalRecords];

            // Header.
            packet[0] = (byte)(_transactionId >> 8);
            packet[1] = (byte)_transactionId;
            packet[2] = 0x84; packet[3] = 0x00;  // QR + AA flags
            packet[4] = (byte)(_qname is null ? 0 : 0);
            packet[5] = (byte)(_qname is null ? 0 : 1);  // QDCOUNT
            packet[6] = (byte)(_answers.Count >> 8);
            packet[7] = (byte)_answers.Count;
            packet[8] = 0; packet[9] = 0;  // NSCOUNT
            packet[10] = (byte)(_additionals.Count >> 8);
            packet[11] = (byte)_additionals.Count;

            var cursor = 12;
            for (var i = 0; i < question.Count; i++) packet[cursor++] = question[i];
            foreach (var rec in _answers) {
                rec.CopyTo(packet, cursor);
                cursor += rec.Length;
            }
            foreach (var rec in _additionals) {
                rec.CopyTo(packet, cursor);
                cursor += rec.Length;
            }
            return packet;
        }
    }
}
