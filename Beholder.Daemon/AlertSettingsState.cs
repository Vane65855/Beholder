using Beholder.Core;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon;

/// <summary>
/// Default <see cref="IAlertSettingsState"/> implementation. Mirrors
/// <see cref="HostnameResolutionSettingsState"/>'s shape: lock-protected
/// backing fields, atomic <c>SetSettings</c> across the section's three
/// bools, event fires outside the lock to prevent re-entrancy deadlocks.
/// </summary>
internal sealed class AlertSettingsState : IAlertSettingsState {
    private readonly object _gate = new();
    private bool _enableNewProcessDetection;
    private bool _enableHashChangeDetection;
    private bool _enableChainIntegrityMonitor;

    public AlertSettingsState(IOptions<AlertOptions> options) {
        ArgumentNullException.ThrowIfNull(options);
        _enableNewProcessDetection = options.Value.EnableNewProcessDetection;
        _enableHashChangeDetection = options.Value.EnableHashChangeDetection;
        _enableChainIntegrityMonitor = options.Value.EnableChainIntegrityMonitor;
    }

    public bool EnableNewProcessDetection {
        get { lock (_gate) return _enableNewProcessDetection; }
    }

    public bool EnableHashChangeDetection {
        get { lock (_gate) return _enableHashChangeDetection; }
    }

    public bool EnableChainIntegrityMonitor {
        get { lock (_gate) return _enableChainIntegrityMonitor; }
    }

    public bool SetSettings(
        bool enableNewProcessDetection,
        bool enableHashChangeDetection,
        bool enableChainIntegrityMonitor
    ) {
        bool changed;
        lock (_gate) {
            changed = _enableNewProcessDetection != enableNewProcessDetection
                   || _enableHashChangeDetection != enableHashChangeDetection
                   || _enableChainIntegrityMonitor != enableChainIntegrityMonitor;
            _enableNewProcessDetection = enableNewProcessDetection;
            _enableHashChangeDetection = enableHashChangeDetection;
            _enableChainIntegrityMonitor = enableChainIntegrityMonitor;
        }
        if (changed) {
            StateChanged?.Invoke(new AlertSettingsSnapshot(
                enableNewProcessDetection,
                enableHashChangeDetection,
                enableChainIntegrityMonitor));
        }
        return changed;
    }

    public event Action<AlertSettingsSnapshot>? StateChanged;
}
