using Beholder.Core;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon;

/// <summary>
/// Default <see cref="IScannerSettingsState"/> implementation. Mirrors
/// <see cref="RecordingSettingsState"/>'s shape: lock-protected backing
/// field, idempotent <c>SetSettings</c>, event fires outside the lock.
/// </summary>
internal sealed class ScannerSettingsState : IScannerSettingsState {
    private readonly object _gate = new();
    private bool _enableHostnameResolution;

    public ScannerSettingsState(IOptions<ScannerOptions> options) {
        ArgumentNullException.ThrowIfNull(options);
        _enableHostnameResolution = options.Value.EnableHostnameResolution;
    }

    public bool EnableHostnameResolution {
        get { lock (_gate) return _enableHostnameResolution; }
    }

    public bool SetSettings(bool enableHostnameResolution) {
        bool changed;
        lock (_gate) {
            changed = _enableHostnameResolution != enableHostnameResolution;
            _enableHostnameResolution = enableHostnameResolution;
        }
        if (changed) {
            StateChanged?.Invoke(new ScannerSettingsSnapshot(enableHostnameResolution));
        }
        return changed;
    }

    public event Action<ScannerSettingsSnapshot>? StateChanged;
}
