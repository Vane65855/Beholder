namespace Beholder.Core;

/// <summary>
/// A single point on a traffic time series: the total bytes in and out during a
/// discrete time interval. Used as the return type for timeline queries where per-
/// destination breakdown is not needed.
/// </summary>
public sealed record TrafficTimePoint {
    /// <summary>Start of the time interval this point represents.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Total bytes received during this interval.</summary>
    public long BytesIn { get; }

    /// <summary>Total bytes sent during this interval.</summary>
    public long BytesOut { get; }

    public TrafficTimePoint(DateTimeOffset timestamp, long bytesIn, long bytesOut) {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesIn);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesOut);

        Timestamp = timestamp;
        BytesIn = bytesIn;
        BytesOut = bytesOut;
    }
}
