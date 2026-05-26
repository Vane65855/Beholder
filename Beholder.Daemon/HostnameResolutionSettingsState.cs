using Beholder.Core;
using Beholder.Daemon.Windows;
using Microsoft.Extensions.Options;

namespace Beholder.Daemon;

/// <summary>
/// Default <see cref="IHostnameResolutionSettingsState"/> implementation.
/// Seeds initial values from <see cref="DnsOptions"/> + <see cref="SniOptions"/>;
/// after that the only mutation path is <see cref="SetSettings"/>.
/// </summary>
internal sealed class HostnameResolutionSettingsState : IHostnameResolutionSettingsState {
    private readonly object _gate = new();
    private bool _enablePreload;
    private bool _enableReverseDnsFallback;
    private bool _enableSniCapture;

    public HostnameResolutionSettingsState(
        IOptions<DnsOptions> dnsOptions,
        IOptions<SniOptions> sniOptions
    ) {
        ArgumentNullException.ThrowIfNull(dnsOptions);
        ArgumentNullException.ThrowIfNull(sniOptions);
        _enablePreload = dnsOptions.Value.EnablePreload;
        _enableReverseDnsFallback = dnsOptions.Value.EnableReverseDnsFallback;
        _enableSniCapture = sniOptions.Value.EnableSniCapture;
    }

    public bool EnablePreload {
        get { lock (_gate) return _enablePreload; }
    }

    public bool EnableReverseDnsFallback {
        get { lock (_gate) return _enableReverseDnsFallback; }
    }

    public bool EnableSniCapture {
        get { lock (_gate) return _enableSniCapture; }
    }

    public bool SetSettings(bool enablePreload, bool enableReverseDnsFallback, bool enableSniCapture) {
        bool changed;
        lock (_gate) {
            changed = _enablePreload != enablePreload
                   || _enableReverseDnsFallback != enableReverseDnsFallback
                   || _enableSniCapture != enableSniCapture;
            _enablePreload = enablePreload;
            _enableReverseDnsFallback = enableReverseDnsFallback;
            _enableSniCapture = enableSniCapture;
        }
        if (changed) {
            StateChanged?.Invoke(new HostnameResolutionSettingsSnapshot(
                enablePreload, enableReverseDnsFallback, enableSniCapture));
        }
        return changed;
    }

    public event Action<HostnameResolutionSettingsSnapshot>? StateChanged;
}
