using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IScannerSettingsState"/>. Mirrors the production
/// class's contract (idempotent <c>SetSettings</c>, event raised only on real
/// transitions) without the production lock — tests are single-threaded.
/// </summary>
internal sealed class FakeScannerSettingsState : IScannerSettingsState {
    public FakeScannerSettingsState(bool initialEnableHostnameResolution = true) {
        EnableHostnameResolution = initialEnableHostnameResolution;
    }

    public bool EnableHostnameResolution { get; private set; }

    public bool SetSettings(bool enableHostnameResolution) {
        if (EnableHostnameResolution == enableHostnameResolution) return false;
        EnableHostnameResolution = enableHostnameResolution;
        StateChanged?.Invoke(new ScannerSettingsSnapshot(enableHostnameResolution));
        return true;
    }

    public event Action<ScannerSettingsSnapshot>? StateChanged;
}
