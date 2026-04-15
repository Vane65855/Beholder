namespace Beholder.Core;

/// <summary>
/// A single persisted row in any tier of the rollup cascade: the aggregate of
/// all traffic between one process and one (remote address, port) destination
/// during a single time window. <see cref="BucketSeconds"/> identifies the tier
/// (1 = raw, 10/60/600/3600 = rolled tiers). The raw tier is written directly
/// by the engine; coarser tiers are populated by the rollup service via
/// <c>INSERT ... SELECT</c> cascades. See <c>docs/ARCHITECTURE.md</c>
/// "Storage Rollup Architecture" for the cascade shape and rollup invariant.
/// </summary>
public sealed record TrafficBucket {
    /// <summary>Database row identifier (0 for unsaved buckets).</summary>
    public long Id { get; }

    /// <summary>Full filesystem path of the process binary.</summary>
    public string ProcessPath { get; }

    /// <summary>Executable file name (e.g. "firefox.exe").</summary>
    public string ProcessName { get; }

    /// <summary>Remote endpoint address as a string (IPv4 or IPv6).</summary>
    public string RemoteAddress { get; }

    /// <summary>Remote endpoint port.</summary>
    public int RemotePort { get; }

    /// <summary>
    /// DNS hostname associated with <see cref="RemoteAddress"/> at the time the
    /// bucket was flushed. Null when the DNS cache had no entry for this address.
    /// </summary>
    public string? Hostname { get; }

    /// <summary>Country code of the remote address, resolved via GeoIP.</summary>
    public CountryCode Country { get; }

    /// <summary>Bytes received from this destination during this bucket window.</summary>
    public long BytesIn { get; }

    /// <summary>Bytes sent to this destination during this bucket window.</summary>
    public long BytesOut { get; }

    /// <summary>Start of the time window, aligned to the bucket boundary.</summary>
    public DateTimeOffset BucketStart { get; }

    /// <summary>Duration of the bucket in seconds (10 for this tier).</summary>
    public int BucketSeconds { get; }

    public TrafficBucket(
        long id,
        string processPath,
        string processName,
        string remoteAddress,
        int remotePort,
        string? hostname,
        CountryCode country,
        long bytesIn,
        long bytesOut,
        DateTimeOffset bucketStart,
        int bucketSeconds
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteAddress);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesIn);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesOut);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketSeconds);

        Id = id;
        ProcessPath = processPath;
        ProcessName = processName;
        RemoteAddress = remoteAddress;
        RemotePort = remotePort;
        Hostname = hostname;
        Country = country;
        BytesIn = bytesIn;
        BytesOut = bytesOut;
        BucketStart = bucketStart;
        BucketSeconds = bucketSeconds;
    }
}
