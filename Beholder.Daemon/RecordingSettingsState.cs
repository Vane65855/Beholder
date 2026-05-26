using Beholder.Core;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon;

/// <summary>
/// Default <see cref="IRecordingSettingsState"/> implementation. Mirrors
/// <see cref="FirewallEnforcementState"/>'s shape: lock-protected backing
/// field, <c>SetSettings</c> returns true on real transitions, event fires
/// outside the lock to prevent re-entrancy deadlocks.
/// </summary>
internal sealed class RecordingSettingsState : IRecordingSettingsState {
    private readonly object _gate = new();
    private bool _filterSelfTraffic;

    public RecordingSettingsState(IOptions<RecordingOptions> options) {
        ArgumentNullException.ThrowIfNull(options);
        _filterSelfTraffic = options.Value.FilterSelfTraffic;
    }

    public bool FilterSelfTraffic {
        get { lock (_gate) return _filterSelfTraffic; }
    }

    public bool SetSettings(bool filterSelfTraffic) {
        bool changed;
        lock (_gate) {
            changed = _filterSelfTraffic != filterSelfTraffic;
            _filterSelfTraffic = filterSelfTraffic;
        }
        if (changed) StateChanged?.Invoke(new RecordingSettingsSnapshot(filterSelfTraffic));
        return changed;
    }

    public event Action<RecordingSettingsSnapshot>? StateChanged;
}
