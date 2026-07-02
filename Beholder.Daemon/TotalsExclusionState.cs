using Beholder.Core;

namespace Beholder.Daemon;

/// <summary>
/// Default <see cref="ITotalsExclusionState"/> implementation. Mirrors
/// <see cref="AlertSettingsState"/>'s shape: lock-protected backing state,
/// atomic set, event fires outside the lock to prevent re-entrancy deadlocks.
/// Holds the list twice — an ordered array for display echoes and an
/// <see cref="StringComparer.OrdinalIgnoreCase"/> set for the per-snapshot
/// <see cref="IsExcluded"/> checks.
/// </summary>
internal sealed class TotalsExclusionState : ITotalsExclusionState {
    private readonly object _gate = new();
    private string[] _paths = [];
    private HashSet<string> _pathSet = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ExcludedProcessPaths {
        get { lock (_gate) return _paths; }
    }

    public bool IsExcluded(string processPath) {
        if (string.IsNullOrEmpty(processPath)) return false;
        lock (_gate) return _pathSet.Contains(processPath);
    }

    public bool SetExcludedPaths(IReadOnlyList<string> excludedProcessPaths) {
        ArgumentNullException.ThrowIfNull(excludedProcessPaths);
        var newSet = new HashSet<string>(excludedProcessPaths, StringComparer.OrdinalIgnoreCase);
        string[] snapshot;
        bool changed;
        lock (_gate) {
            changed = !_pathSet.SetEquals(newSet);
            _paths = [.. excludedProcessPaths];
            _pathSet = newSet;
            snapshot = _paths;
        }
        if (changed) StateChanged?.Invoke(new TotalsExclusionSnapshot(snapshot));
        return changed;
    }

    public event Action<TotalsExclusionSnapshot>? StateChanged;
}
