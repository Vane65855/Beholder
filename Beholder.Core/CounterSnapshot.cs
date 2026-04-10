namespace Beholder.Core;

/// <summary>
/// A per-process traffic aggregate emitted by the daemon's accumulator on every tick.
/// Includes both cumulative totals and the delta since the previous tick, plus a
/// per-country breakdown of outbound bytes.
/// </summary>
public sealed record CounterSnapshot {
    /// <summary>Executable file name of the process this snapshot describes.</summary>
    public string ProcessName { get; }

    /// <summary>Full filesystem path of the binary.</summary>
    public string ProcessPath { get; }

    /// <summary>Cumulative inbound bytes since the daemon started observing this process.</summary>
    public long TotalBytesIn { get; }

    /// <summary>Cumulative outbound bytes since the daemon started observing this process.</summary>
    public long TotalBytesOut { get; }

    /// <summary>Inbound bytes observed during the most recent tick window.</summary>
    public long DeltaBytesIn { get; }

    /// <summary>Outbound bytes observed during the most recent tick window.</summary>
    public long DeltaBytesOut { get; }

    /// <summary>Number of distinct active connections held by this process.</summary>
    public int ActiveConnectionCount { get; }

    /// <summary>Cumulative outbound bytes broken down by destination country.</summary>
    public IReadOnlyDictionary<CountryCode, long> BytesOutByCountry { get; }

    /// <summary>Wall-clock timestamp of the tick that produced this snapshot.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Constructs a validated counter snapshot.</summary>
    public CounterSnapshot(
        string processName,
        string processPath,
        long totalBytesIn,
        long totalBytesOut,
        long deltaBytesIn,
        long deltaBytesOut,
        int activeConnectionCount,
        IReadOnlyDictionary<CountryCode, long> bytesOutByCountry,
        DateTimeOffset timestamp
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentNullException.ThrowIfNull(bytesOutByCountry);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytesIn);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytesOut);
        ArgumentOutOfRangeException.ThrowIfNegative(deltaBytesIn);
        ArgumentOutOfRangeException.ThrowIfNegative(deltaBytesOut);

        ProcessName = processName;
        ProcessPath = processPath;
        TotalBytesIn = totalBytesIn;
        TotalBytesOut = totalBytesOut;
        DeltaBytesIn = deltaBytesIn;
        DeltaBytesOut = deltaBytesOut;
        ActiveConnectionCount = activeConnectionCount;
        BytesOutByCountry = bytesOutByCountry;
        Timestamp = timestamp;
    }
}
