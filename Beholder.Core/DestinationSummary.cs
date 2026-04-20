namespace Beholder.Core;

/// <summary>
/// Aggregated traffic to a single remote address over a queried time range.
/// Produced by <see cref="ITrafficStore.GetDestinationsAsync"/> — the
/// cumulative per-destination totals are reconstructed from SQLite, not held
/// in memory.
/// </summary>
public sealed record DestinationSummary {
    /// <summary>Remote endpoint address as a string (IPv4 or IPv6).</summary>
    public string RemoteAddress { get; }

    /// <summary>
    /// Most recent DNS hostname associated with this address, or null if no
    /// hostname was captured by the DNS cache at flush time.
    /// </summary>
    public string? Hostname { get; }

    /// <summary>Country code of the remote address.</summary>
    public CountryCode Country { get; }

    /// <summary>Total bytes received from this address in the queried range.</summary>
    public long TotalBytesIn { get; }

    /// <summary>Total bytes sent to this address in the queried range.</summary>
    public long TotalBytesOut { get; }

    /// <summary>Count of distinct remote ports contacted at this address.</summary>
    public int ConnectionCount { get; }

    public DestinationSummary(
        string remoteAddress,
        string? hostname,
        CountryCode country,
        long totalBytesIn,
        long totalBytesOut,
        int connectionCount
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteAddress);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytesIn);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytesOut);
        ArgumentOutOfRangeException.ThrowIfNegative(connectionCount);

        RemoteAddress = remoteAddress;
        Hostname = hostname;
        Country = country;
        TotalBytesIn = totalBytesIn;
        TotalBytesOut = totalBytesOut;
        ConnectionCount = connectionCount;
    }
}
