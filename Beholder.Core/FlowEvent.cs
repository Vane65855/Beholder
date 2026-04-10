using System.Net;

namespace Beholder.Core;

/// <summary>
/// A single observed network event from the platform provider, after GeoIP resolution
/// has attached a country code. This is the unit that travels through the daemon's
/// flow pipeline before per-process aggregation.
/// </summary>
public sealed record FlowEvent {
    /// <summary>OS process identifier of the local endpoint.</summary>
    public int ProcessId { get; }

    /// <summary>Executable file name (e.g. "firefox.exe").</summary>
    public string ProcessName { get; }

    /// <summary>Full filesystem path to the binary.</summary>
    public string ProcessPath { get; }

    /// <summary>Remote endpoint address.</summary>
    public IPAddress RemoteAddress { get; }

    /// <summary>Remote endpoint port.</summary>
    public int RemotePort { get; }

    /// <summary>Bytes received from the remote endpoint during this event.</summary>
    public long BytesIn { get; }

    /// <summary>Bytes sent to the remote endpoint during this event.</summary>
    public long BytesOut { get; }

    /// <summary>Country attached to <see cref="RemoteAddress"/> by the GeoIP resolver.</summary>
    public CountryCode Country { get; }

    /// <summary>Wall-clock timestamp at which the platform provider observed the event.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Constructs a validated flow event.</summary>
    public FlowEvent(
        int processId,
        string processName,
        string processPath,
        IPAddress remoteAddress,
        int remotePort,
        long bytesIn,
        long bytesOut,
        CountryCode country,
        DateTimeOffset timestamp
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentNullException.ThrowIfNull(remoteAddress);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesIn);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesOut);

        ProcessId = processId;
        ProcessName = processName;
        ProcessPath = processPath;
        RemoteAddress = remoteAddress;
        RemotePort = remotePort;
        BytesIn = bytesIn;
        BytesOut = bytesOut;
        Country = country;
        Timestamp = timestamp;
    }
}
