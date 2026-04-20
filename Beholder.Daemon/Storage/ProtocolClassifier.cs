namespace Beholder.Daemon.Storage;

/// <summary>
/// Maps remote ports to human-readable protocol names for the Traffic tab's
/// "Traffic Type" breakdown. Unknown ports bucket into a single <c>"Other"</c>
/// label so p2p/ephemeral-port traffic (BitTorrent peers, WebRTC, etc.) doesn't
/// explode the list into dozens of unhelpful <c>"Port 60387"</c> rows. Kept as
/// a static lookup — the table is small and changes rarely (if at all) across
/// releases.
/// </summary>
/// <remarks>
/// This is a port-based classifier, not deep packet inspection. It correctly
/// identifies well-known services (HTTPS on 443, DNS on 53, etc.) but can't
/// tell you what an ephemeral-port peer connection actually carries — those
/// land under "Other". Process-aware heuristics (labelling traffic from
/// <c>qbittorrent.exe</c> as BitTorrent regardless of port) are a possible
/// future extension but would couple the classifier to process metadata.
/// </remarks>
internal static class ProtocolClassifier {
    public const string OtherLabel = "Other";

    private static readonly Dictionary<int, string> WellKnownPorts = new() {
        [21] = "FTP",
        [22] = "SSH",
        [25] = "SMTP",
        [53] = "DNS",
        [80] = "HTTP",
        [110] = "POP3",
        [123] = "NTP",
        [143] = "IMAP",
        [161] = "SNMP",
        [162] = "SNMP",
        [443] = "HTTPS",
        [465] = "SMTPS",
        [587] = "SMTPS",
        [993] = "IMAPS",
        [995] = "POP3S",
        [1194] = "OpenVPN",
        [1935] = "RTMP",
        [3389] = "RDP",
        [5060] = "SIP",
        [5353] = "mDNS",
        [51820] = "WireGuard",
    };

    /// <summary>
    /// Returns the protocol name for <paramref name="remotePort"/>, or
    /// <see cref="OtherLabel"/> if the port is not in the well-known list.
    /// All unrecognised ports classify to the same label so the caller's
    /// name-keyed aggregation collapses them into a single row.
    /// </summary>
    public static string Classify(int remotePort) =>
        WellKnownPorts.TryGetValue(remotePort, out var name)
            ? name
            : OtherLabel;
}
