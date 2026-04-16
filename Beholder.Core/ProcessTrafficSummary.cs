namespace Beholder.Core;

/// <summary>
/// Per-process aggregated traffic totals over a queried time range. Produced by
/// <see cref="ITrafficStore.GetProcessSummariesAsync"/> — the totals are
/// reconstructed from whichever rollup tier covers the requested range, so
/// historically-active processes that the engine has evicted from its in-memory
/// working set still appear.
/// </summary>
public sealed record ProcessTrafficSummary {
    /// <summary>Full filesystem path of the process binary.</summary>
    public string ProcessPath { get; }

    /// <summary>Executable file name (e.g. "firefox.exe").</summary>
    public string ProcessName { get; }

    /// <summary>Total bytes received by this process in the queried range.</summary>
    public long TotalBytesIn { get; }

    /// <summary>Total bytes sent by this process in the queried range.</summary>
    public long TotalBytesOut { get; }

    public ProcessTrafficSummary(
        string processPath,
        string processName,
        long totalBytesIn,
        long totalBytesOut
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytesIn);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytesOut);

        ProcessPath = processPath;
        ProcessName = processName;
        TotalBytesIn = totalBytesIn;
        TotalBytesOut = totalBytesOut;
    }
}
