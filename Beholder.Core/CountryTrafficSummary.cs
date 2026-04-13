namespace Beholder.Core;

/// <summary>
/// Aggregated traffic to a single country over a queried time range. Produced by
/// <see cref="ITrafficStore.GetCountryBreakdownAsync"/> for the map tab and
/// geographic analysis views.
/// </summary>
public sealed record CountryTrafficSummary {
    /// <summary>Two-letter country code.</summary>
    public CountryCode Country { get; }

    /// <summary>Total bytes received from this country in the queried range.</summary>
    public long TotalBytesIn { get; }

    /// <summary>Total bytes sent to this country in the queried range.</summary>
    public long TotalBytesOut { get; }

    public CountryTrafficSummary(CountryCode country, long totalBytesIn, long totalBytesOut) {
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytesIn);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytesOut);

        Country = country;
        TotalBytesIn = totalBytesIn;
        TotalBytesOut = totalBytesOut;
    }
}
