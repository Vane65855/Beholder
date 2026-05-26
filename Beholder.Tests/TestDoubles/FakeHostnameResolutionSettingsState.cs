using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IHostnameResolutionSettingsState"/>. Mirrors the
/// production class's contract (atomic <c>SetSettings</c> across all three
/// values, event raised only on real transitions) without the production lock
/// — tests are single-threaded.
/// </summary>
internal sealed class FakeHostnameResolutionSettingsState : IHostnameResolutionSettingsState {
    public FakeHostnameResolutionSettingsState(
        bool initialEnablePreload = true,
        bool initialEnableReverseDnsFallback = true,
        bool initialEnableSniCapture = true
    ) {
        EnablePreload = initialEnablePreload;
        EnableReverseDnsFallback = initialEnableReverseDnsFallback;
        EnableSniCapture = initialEnableSniCapture;
    }

    public bool EnablePreload { get; private set; }
    public bool EnableReverseDnsFallback { get; private set; }
    public bool EnableSniCapture { get; private set; }

    public bool SetSettings(bool enablePreload, bool enableReverseDnsFallback, bool enableSniCapture) {
        if (EnablePreload == enablePreload
            && EnableReverseDnsFallback == enableReverseDnsFallback
            && EnableSniCapture == enableSniCapture) {
            return false;
        }
        EnablePreload = enablePreload;
        EnableReverseDnsFallback = enableReverseDnsFallback;
        EnableSniCapture = enableSniCapture;
        StateChanged?.Invoke(new HostnameResolutionSettingsSnapshot(
            EnablePreload: enablePreload,
            EnableReverseDnsFallback: enableReverseDnsFallback,
            EnableSniCapture: enableSniCapture));
        return true;
    }

    public event Action<HostnameResolutionSettingsSnapshot>? StateChanged;
}
