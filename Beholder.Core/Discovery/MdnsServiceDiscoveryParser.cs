namespace Beholder.Core.Discovery;

/// <summary>
/// Parses RFC 6763 DNS-Based Service Discovery (DNS-SD) response packets
/// over mDNS, extracting the responding device's hostname. Mirrors
/// <see cref="Beholder.Core.Tls.TlsClientHelloParser"/>'s defensive shape:
/// bounds-check every length field, return <see langword="false"/> with
/// no allocation and no exception on any malformed input.
/// </summary>
/// <remarks>
/// <para>
/// An mDNS-SD response packet typically carries 3 record types per
/// service instance:
/// <list type="bullet">
///   <item><b>PTR</b>: service-type → instance name (e.g.
///     <c>_airplay._tcp.local PTR Living\032Room\032TV._airplay._tcp.local</c>)</item>
///   <item><b>SRV</b>: instance name → target hostname + port (e.g.
///     <c>Living\032Room\032TV._airplay._tcp.local SRV 0 0 7000 living-room-tv.local</c>)</item>
///   <item><b>A</b>: target hostname → IPv4 (e.g.
///     <c>living-room-tv.local A 192.168.1.42</c>)</item>
/// </list>
/// </para>
///
/// <para>
/// Hostname extraction priority — first success wins:
/// <list type="number">
///   <item>SRV record's target (best — explicit hostname like <c>living-room-tv.local</c>)</item>
///   <item>Any A record's owner name (same shape; usually identical to SRV target)</item>
///   <item>PTR record's instance leftmost label (fallback when no SRV/A
///     is present; preserves spaces in the instance name)</item>
/// </list>
/// The trailing <c>.local</c> suffix is stripped from options 1 and 2.
/// </para>
///
/// <para>
/// IP attribution is the caller's responsibility — typically the source
/// address of the UDP packet (<c>UdpReceiveResult.RemoteEndPoint.Address</c>).
/// We don't extract it from A record RDATA because real-world responses
/// reliably reflect the responding device in their source IP.
/// </para>
/// </remarks>
public static class MdnsServiceDiscoveryParser {
    private const ushort RecordTypeA = 0x0001;
    private const ushort RecordTypePtr = 0x000C;
    private const ushort RecordTypeSrv = 0x0021;
    private const int DnsHeaderLength = 12;
    private const int SrvFixedFieldsLength = 6;  // priority(2) + weight(2) + port(2) before the target name

    /// <summary>
    /// Attempts to extract the responding device's hostname from a
    /// DNS-SD response packet. Returns <see langword="true"/> with
    /// <paramref name="hostname"/> set when the packet's transaction ID
    /// matches one of <paramref name="expectedTransactionIds"/> and at
    /// least one usable hostname is found in the answer / additional
    /// sections. Returns <see langword="false"/> otherwise — wrong TID,
    /// truncated buffer, no usable records, all parsing attempts failed.
    /// </summary>
    public static bool TryExtractHostname(
        ReadOnlySpan<byte> packet,
        IReadOnlySet<ushort> expectedTransactionIds,
        out string? hostname
    ) {
        hostname = null;
        ArgumentNullException.ThrowIfNull(expectedTransactionIds);
        if (packet.Length < DnsHeaderLength) return false;

        var transactionId = ReadU16(packet, 0);
        if (!expectedTransactionIds.Contains(transactionId)) return false;

        var questionCount = ReadU16(packet, 4);
        var answerCount = ReadU16(packet, 6);
        var authorityCount = ReadU16(packet, 8);
        var additionalCount = ReadU16(packet, 10);
        var totalRecords = answerCount + authorityCount + additionalCount;
        if (totalRecords == 0) return false;

        // Skip the question section.
        var p = DnsHeaderLength;
        for (var i = 0; i < questionCount; i++) {
            if (!DnsNameDecoder.TrySkipName(packet, ref p)) return false;
            if (p + 4 > packet.Length) return false;  // QTYPE + QCLASS
            p += 4;
        }

        // Walk all records (answers + authority + additional). Collect a
        // hostname candidate from the first SRV target we see; otherwise
        // hold onto an A record's owner name and a PTR instance leftmost
        // label as fallbacks.
        string? srvTarget = null;
        string? aOwner = null;
        string? ptrInstanceLabel = null;

        for (var i = 0; i < totalRecords; i++) {
            // NAME (variable; may be a compression pointer back to the question)
            var ownerNameStart = p;
            if (!DnsNameDecoder.TryReadName(packet, ref p, out var ownerName, strict: true)) {
                // Owner-name unreadable under strict mode — skip past it via
                // the skip-only path and continue with the next record.
                p = ownerNameStart;
                if (!DnsNameDecoder.TrySkipName(packet, ref p)) return false;
                ownerName = null;
            }
            if (p + 10 > packet.Length) return false;  // TYPE + CLASS + TTL + RDLENGTH

            var recordType = ReadU16(packet, p);
            var rdLength = ReadU16(packet, p + 8);
            p += 10;
            if (p + rdLength > packet.Length) return false;

            switch (recordType) {
                case RecordTypeSrv when srvTarget is null && rdLength >= SrvFixedFieldsLength + 1:
                    // RDATA = priority(2) + weight(2) + port(2) + target-name
                    var srvTargetOffset = p + SrvFixedFieldsLength;
                    if (DnsNameDecoder.TryReadName(packet, ref srvTargetOffset, out var srvName, strict: true)
                        && !string.IsNullOrEmpty(srvName)) {
                        srvTarget = StripTrailingLocal(srvName);
                    }
                    break;

                case RecordTypeA when aOwner is null && !string.IsNullOrEmpty(ownerName):
                    aOwner = StripTrailingLocal(ownerName);
                    break;

                case RecordTypePtr when ptrInstanceLabel is null:
                    if (DnsNameDecoder.TryReadFirstLabel(packet, p, out var instanceLabel)
                        && !string.IsNullOrEmpty(instanceLabel)) {
                        ptrInstanceLabel = instanceLabel;
                    }
                    break;
            }

            p += rdLength;

            // Short-circuit: once we have a SRV target, no fallback will
            // outrank it. Stop walking the rest of the records.
            if (srvTarget is not null) break;
        }

        hostname = srvTarget ?? aOwner ?? ptrInstanceLabel;
        return !string.IsNullOrEmpty(hostname);
    }

    /// <summary>
    /// Strips a trailing <c>.local</c> (case-insensitive, with optional
    /// trailing dot) from <paramref name="name"/>. <c>iphone.local</c> →
    /// <c>iphone</c>; <c>iphone.local.</c> → <c>iphone</c>;
    /// <c>iphone</c> → <c>iphone</c> (unchanged).
    /// </summary>
    private static string StripTrailingLocal(string name) {
        var trimmed = name.EndsWith('.') ? name[..^1] : name;
        if (trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) {
            trimmed = trimmed[..^".local".Length];
        }
        return trimmed;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);
}
