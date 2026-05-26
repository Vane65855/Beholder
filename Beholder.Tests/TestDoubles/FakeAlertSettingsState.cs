using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IAlertSettingsState"/>. Mirrors the production
/// class's contract (atomic <c>SetSettings</c> across all three bools, event
/// raised only on real transitions) without the production lock — tests are
/// single-threaded.
/// </summary>
internal sealed class FakeAlertSettingsState : IAlertSettingsState {
    public FakeAlertSettingsState(
        bool initialEnableNewProcessDetection = true,
        bool initialEnableHashChangeDetection = true,
        bool initialEnableChainIntegrityMonitor = true
    ) {
        EnableNewProcessDetection = initialEnableNewProcessDetection;
        EnableHashChangeDetection = initialEnableHashChangeDetection;
        EnableChainIntegrityMonitor = initialEnableChainIntegrityMonitor;
    }

    public bool EnableNewProcessDetection { get; private set; }
    public bool EnableHashChangeDetection { get; private set; }
    public bool EnableChainIntegrityMonitor { get; private set; }

    public bool SetSettings(
        bool enableNewProcessDetection,
        bool enableHashChangeDetection,
        bool enableChainIntegrityMonitor
    ) {
        if (EnableNewProcessDetection == enableNewProcessDetection
            && EnableHashChangeDetection == enableHashChangeDetection
            && EnableChainIntegrityMonitor == enableChainIntegrityMonitor) {
            return false;
        }
        EnableNewProcessDetection = enableNewProcessDetection;
        EnableHashChangeDetection = enableHashChangeDetection;
        EnableChainIntegrityMonitor = enableChainIntegrityMonitor;
        StateChanged?.Invoke(new AlertSettingsSnapshot(
            EnableNewProcessDetection: enableNewProcessDetection,
            EnableHashChangeDetection: enableHashChangeDetection,
            EnableChainIntegrityMonitor: enableChainIntegrityMonitor));
        return true;
    }

    public event Action<AlertSettingsSnapshot>? StateChanged;
}
