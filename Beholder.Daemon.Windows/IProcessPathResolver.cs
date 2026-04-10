namespace Beholder.Daemon.Windows;

/// <summary>
/// Maps an OS process ID to a human-readable name and full binary path. Extracted
/// from <see cref="EtwFlowSource"/> so the ETW event parser does not depend directly
/// on <see cref="System.Diagnostics.Process"/>, and can be swapped out in tests or
/// on hosts where process enumeration is restricted.
/// </summary>
internal interface IProcessPathResolver {
    /// <summary>
    /// Returns the process name and full filesystem path for the given PID. Returns
    /// <c>("unknown", "unknown")</c> when the process has exited or cannot be queried.
    /// Implementations must be thread-safe — ETW event callbacks arrive on a dedicated
    /// trace-processing thread and may call this from any thread.
    /// </summary>
    (string Name, string Path) Resolve(int processId);
}
