namespace Beholder.Core;

/// <summary>
/// Aggregated traffic grouped by protocol (derived from remote port). Produced
/// by <see cref="ITrafficStore.GetProtocolBreakdownAsync"/> for the Traffic
/// tab's COLS view "Traffic Type" column.
/// </summary>
/// <remarks>
/// The <see cref="ProtocolName"/> is the daemon's port→name classification —
/// callers don't re-derive it. Well-known ports map to their protocol name
/// ("HTTPS" for 443, "DNS" for 53). Every unrecognised port buckets into a
/// single <c>"Other"</c> row so p2p/ephemeral traffic (BitTorrent peers,
/// WebRTC, etc.) doesn't explode the list. All transports are currently
/// reported as <c>"TCP"</c>; UDP support is deferred until the capture
/// engine grows UDP flow tracking.
/// </remarks>
public sealed record ProtocolBreakdownSummary {
    /// <summary>Human-readable protocol name (e.g. "HTTPS", "DNS", "Port 8080").</summary>
    public string ProtocolName { get; }

    /// <summary>Transport protocol. Currently always "TCP".</summary>
    public string Transport { get; }

    /// <summary>Total bytes received over this protocol in the queried range.</summary>
    public long TotalBytesIn { get; }

    /// <summary>Total bytes sent over this protocol in the queried range.</summary>
    public long TotalBytesOut { get; }

    public ProtocolBreakdownSummary(
        string protocolName,
        string transport,
        long totalBytesIn,
        long totalBytesOut
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(transport);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytesIn);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytesOut);

        ProtocolName = protocolName;
        Transport = transport;
        TotalBytesIn = totalBytesIn;
        TotalBytesOut = totalBytesOut;
    }
}
