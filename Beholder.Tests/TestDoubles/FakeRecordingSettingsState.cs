using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IRecordingSettingsState"/>. Mirrors the
/// production class's contract (idempotent <c>SetSettings</c>, event raised
/// only on real transitions) without the production lock — tests are
/// single-threaded.
/// </summary>
internal sealed class FakeRecordingSettingsState : IRecordingSettingsState {
    public FakeRecordingSettingsState(bool initialFilterSelfTraffic = true) {
        FilterSelfTraffic = initialFilterSelfTraffic;
    }

    public bool FilterSelfTraffic { get; private set; }

    public bool SetSettings(bool filterSelfTraffic) {
        if (FilterSelfTraffic == filterSelfTraffic) return false;
        FilterSelfTraffic = filterSelfTraffic;
        StateChanged?.Invoke(new RecordingSettingsSnapshot(filterSelfTraffic));
        return true;
    }

    public event Action<RecordingSettingsSnapshot>? StateChanged;
}
