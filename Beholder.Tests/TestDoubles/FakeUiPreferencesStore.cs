using Beholder.Ui.Models;
using Beholder.Ui.Services;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="IUiPreferencesStore"/>: <see cref="Load"/> returns the
/// current value; <see cref="Save"/> records it (and counts calls) for assertions.
/// </summary>
internal sealed class FakeUiPreferencesStore : IUiPreferencesStore {
    public UiPreferences Current { get; set; } = new();
    public int SaveCount { get; private set; }

    public UiPreferences Load() => Current;

    public void Save(UiPreferences preferences) {
        Current = preferences;
        SaveCount++;
    }
}
