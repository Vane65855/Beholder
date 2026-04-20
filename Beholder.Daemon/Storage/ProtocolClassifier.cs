namespace Beholder.Daemon.Storage;

/// <summary>
/// Maps remote ports to human-readable protocol names for the Traffic tab's
/// "Traffic Type" breakdown. Unknown ports fall back to a <c>"Port {N}"</c>
/// label so every row still has a stable name. Kept as a static lookup — the
/// table is small and changes rarely (if at all) across releases.
/// </summary>
internal static class ProtocolClassifier {
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
    /// Returns a human-readable name for <paramref name="remotePort"/>, or
    /// <c>"Port {remotePort}"</c> if the port is not recognized.
    /// </summary>
    public static string Classify(int remotePort) =>
        WellKnownPorts.TryGetValue(remotePort, out var name)
            ? name
            : $"Port {remotePort}";
}
