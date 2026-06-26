using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Beholder.Core;

namespace Beholder.Daemon.Windows;

/// <summary>
/// Default <see cref="IProcessPathResolver"/> implementation backed by
/// <see cref="Process.GetProcessById(int)"/>. Caches per-PID results in a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> so that repeated ETW events from
/// the same process resolve once. The cache is unbounded — PIDs recycle slowly on
/// Windows and the daemon's lifecycle is machine uptime.
/// </summary>
internal sealed class ProcessPathResolver : IProcessPathResolver {
    private readonly ConcurrentDictionary<int, (string Name, string Path)> _cache = new();

    public (string Name, string Path) Resolve(int processId) {
        return _cache.GetOrAdd(processId, ResolveUncached);
    }

    private static (string Name, string Path) ResolveUncached(int processId) {
        // PID 4 is the Windows System process. Opening it as a regular Process handle
        // either fails or yields limited info depending on token privileges, so we
        // short-circuit with a fixed identity.
        if (processId == 4) return (ProcessSentinels.System, ProcessSentinels.System);

        try {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            try {
                var path = process.MainModule?.FileName ?? ProcessSentinels.Unknown;
                return (name, path);
            } catch (Win32Exception) {
                // Access denied reading MainModule (common for services running as
                // SYSTEM when the daemon lacks cross-process inspection rights).
                // We keep the process name — it came from a cheaper API.
                return (name, ProcessSentinels.Unknown);
            }
        } catch (ArgumentException) {
            // Process already exited between the ETW event and our lookup.
            return (ProcessSentinels.Unknown, ProcessSentinels.Unknown);
        } catch (InvalidOperationException) {
            return (ProcessSentinels.Unknown, ProcessSentinels.Unknown);
        }
    }
}
