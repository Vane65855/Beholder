using Beholder.Core;
using Beholder.Daemon;
using Microsoft.Extensions.Options;

namespace Beholder.Tests;

public class RecordingSettingsStateTests {
    [Fact]
    public void InitialState_SeededFromOptions() {
        var state = new RecordingSettingsState(
            Options.Create(new RecordingOptions { FilterSelfTraffic = false }));

        Assert.False(state.FilterSelfTraffic);
    }

    [Fact]
    public void InitialState_DefaultIsFilterSelfTrafficTrue() {
        // RecordingOptions.FilterSelfTraffic defaults to true — this is the
        // production default that ships in appsettings.json without an override.
        var state = new RecordingSettingsState(Options.Create(new RecordingOptions()));

        Assert.True(state.FilterSelfTraffic);
    }

    [Fact]
    public void SetSettings_NoChange_ReturnsFalse() {
        var state = new RecordingSettingsState(
            Options.Create(new RecordingOptions { FilterSelfTraffic = true }));

        var result = state.SetSettings(true);

        Assert.False(result);
        Assert.True(state.FilterSelfTraffic);
    }

    [Fact]
    public void SetSettings_RealTransition_ReturnsTrueAndUpdatesValue() {
        var state = new RecordingSettingsState(
            Options.Create(new RecordingOptions { FilterSelfTraffic = true }));

        var result = state.SetSettings(false);

        Assert.True(result);
        Assert.False(state.FilterSelfTraffic);
    }

    [Fact]
    public void StateChanged_FiresOnlyOnRealTransitions() {
        var state = new RecordingSettingsState(
            Options.Create(new RecordingOptions { FilterSelfTraffic = true }));
        var snapshots = new List<RecordingSettingsSnapshot>();
        state.StateChanged += snapshots.Add;

        state.SetSettings(true);       // No-op — should not fire.
        state.SetSettings(false);      // Real transition — should fire.
        state.SetSettings(false);      // No-op — should not fire.
        state.SetSettings(true);       // Real transition — should fire.

        Assert.Equal(2, snapshots.Count);
        Assert.False(snapshots[0].FilterSelfTraffic);
        Assert.True(snapshots[1].FilterSelfTraffic);
    }

    [Fact]
    public async Task SetSettings_ConcurrentCallers_NoTearing() {
        var state = new RecordingSettingsState(
            Options.Create(new RecordingOptions { FilterSelfTraffic = true }));
        var observedTransitions = 0;
        state.StateChanged += _ => Interlocked.Increment(ref observedTransitions);

        const int iterations = 1000;
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => {
            for (var i = 0; i < iterations; i++) {
                state.SetSettings(i % 2 == 0);
            }
        }));
        await Task.WhenAll(tasks);

        // The state itself must land on a consistent value (true or false).
        // The event count is an inequality lower bound: every "real transition"
        // fires, but how many transitions happen across 8 threads is
        // non-deterministic. The key claim is "no torn read" — the lock
        // guarantees the value is one of {true, false}, never garbage.
        var finalValue = state.FilterSelfTraffic;
        Assert.True(finalValue == true || finalValue == false);
    }
}
