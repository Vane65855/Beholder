namespace Beholder.Core.Discovery;

/// <summary>
/// Builds RFC 6763 DNS-Based Service Discovery (DNS-SD) query packets over
/// mDNS. Per the Phase 9.2.6 design, the scanner sends one PTR query per
/// well-known service type (e.g. <c>_airplay._tcp.local</c>) and devices
/// that advertise that service respond with PTR records pointing to their
/// instance, typically accompanied by SRV + A "additional records" that
/// carry the hostname + IP.
/// </summary>
/// <remarks>
/// The QU bit (RFC 6762 §5.4 — "unicast response preferred") is set on
/// every query so responders unicast their reply back to our ephemeral
/// source port, avoiding port competition with Bonjour-style services
/// that may already own 5353 on the host machine.
/// </remarks>
public static class MdnsServiceDiscoveryPacketBuilder {
    private const ushort QueryTypePtr = 0x000C;     // DNS PTR record type
    private const ushort QueryClassIn = 0x0001;     // DNS IN class
    private const ushort QueryClassQuBit = 0x8000;  // RFC 6762 §5.4 unicast-response flag
    private const int DnsHeaderLength = 12;
    private const int MaxLabelLength = 63;          // RFC 1035 §2.3.4

    /// <summary>
    /// Builds an mDNS PTR query for <paramref name="serviceType"/>, which
    /// must be of the form <c>_&lt;servicename&gt;._&lt;proto&gt;.local</c>
    /// (e.g. <c>"_airplay._tcp.local"</c>). QCLASS carries the QU bit so
    /// responders unicast their reply to the source ephemeral port.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="serviceType"/> is null, empty, doesn't
    /// match the expected shape, or contains a label longer than 63 bytes.
    /// </exception>
    public static byte[] BuildServiceTypeQuery(string serviceType, ushort transactionId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);
        ValidateServiceType(serviceType);

        var labels = serviceType.Split('.');
        var qnameLength = labels.Sum(l => 1 + l.Length) + 1;  // each label: len byte + chars; + root terminator
        var totalLength = DnsHeaderLength + qnameLength + 4;  // +4 = QTYPE + QCLASS
        var packet = new byte[totalLength];

        WriteU16(packet, 0, transactionId);
        WriteU16(packet, 2, 0x0000);   // Flags: standard query, not authoritative, not recursive
        WriteU16(packet, 4, 0x0001);   // QDCOUNT
        WriteU16(packet, 6, 0x0000);   // ANCOUNT
        WriteU16(packet, 8, 0x0000);   // NSCOUNT
        WriteU16(packet, 10, 0x0000);  // ARCOUNT

        var cursor = DnsHeaderLength;
        foreach (var label in labels) {
            packet[cursor++] = (byte)label.Length;
            for (var i = 0; i < label.Length; i++) packet[cursor++] = (byte)label[i];
        }
        packet[cursor++] = 0x00;  // root terminator

        WriteU16(packet, cursor, QueryTypePtr);
        WriteU16(packet, cursor + 2, QueryClassIn | QueryClassQuBit);

        return packet;
    }

    /// <summary>
    /// Verifies <paramref name="serviceType"/> matches the DNS-SD shape
    /// <c>_&lt;servicename&gt;._&lt;proto&gt;.local</c>. Throws
    /// <see cref="ArgumentException"/> on malformed input — this is a
    /// programmer error (a hardcoded service-type string typo), not a
    /// runtime adversarial-input case.
    /// </summary>
    private static void ValidateServiceType(string serviceType) {
        var labels = serviceType.Split('.');
        if (labels.Length != 3) {
            throw new ArgumentException(
                $"Service type '{serviceType}' must be of the form '_<service>._<proto>.local'",
                nameof(serviceType));
        }
        if (!labels[0].StartsWith('_') || labels[0].Length < 2) {
            throw new ArgumentException(
                $"Service type '{serviceType}' first label must start with '_' and have a service name",
                nameof(serviceType));
        }
        if (labels[1] is not "_tcp" and not "_udp") {
            throw new ArgumentException(
                $"Service type '{serviceType}' protocol label must be '_tcp' or '_udp', got '{labels[1]}'",
                nameof(serviceType));
        }
        if (labels[2] != "local") {
            throw new ArgumentException(
                $"Service type '{serviceType}' must end with '.local', got '{labels[2]}'",
                nameof(serviceType));
        }
        foreach (var label in labels) {
            if (label.Length > MaxLabelLength) {
                throw new ArgumentException(
                    $"Service type '{serviceType}' label '{label}' exceeds the {MaxLabelLength}-byte DNS limit",
                    nameof(serviceType));
            }
        }
    }

    private static void WriteU16(byte[] data, int offset, ushort value) {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }
}
