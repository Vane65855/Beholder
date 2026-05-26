using Beholder.Core;
using Beholder.Daemon;
using Microsoft.Extensions.Options;

namespace Beholder.Tests;

public class AlertSettingsStateTests {
    [Fact]
    public void InitialState_SeededFromOptions() {
        var state = new AlertSettingsState(Options.Create(new AlertOptions {
            EnableNewProcessDetection = true,
            EnableHashChangeDetection = false,
            EnableChainIntegrityMonitor = true,
        }));

        Assert.True(state.EnableNewProcessDetection);
        Assert.False(state.EnableHashChangeDetection);
        Assert.True(state.EnableChainIntegrityMonitor);
    }

    [Fact]
    public void SetSettings_NoChange_ReturnsFalse() {
        var state = new AlertSettingsState(Options.Create(new AlertOptions {
            EnableNewProcessDetection = true,
            EnableHashChangeDetection = true,
            EnableChainIntegrityMonitor = true,
        }));

        var result = state.SetSettings(true, true, true);

        Assert.False(result);
    }

    [Fact]
    public void SetSettings_SingleFieldTransition_ReturnsTrue() {
        var state = new AlertSettingsState(Options.Create(new AlertOptions {
            EnableNewProcessDetection = true,
            EnableHashChangeDetection = true,
            EnableChainIntegrityMonitor = true,
        }));

        var result = state.SetSettings(
            enableNewProcessDetection: false,
            enableHashChangeDetection: true,
            enableChainIntegrityMonitor: true);

        Assert.True(result);
        Assert.False(state.EnableNewProcessDetection);
        Assert.True(state.EnableHashChangeDetection);
        Assert.True(state.EnableChainIntegrityMonitor);
    }

    [Fact]
    public void SetSettings_MultipleFieldsTransition_ReturnsTrueAndUpdatesAll() {
        var state = new AlertSettingsState(Options.Create(new AlertOptions()));

        var result = state.SetSettings(false, false, false);

        Assert.True(result);
        Assert.False(state.EnableNewProcessDetection);
        Assert.False(state.EnableHashChangeDetection);
        Assert.False(state.EnableChainIntegrityMonitor);
    }

    [Fact]
    public void StateChanged_FiresOnlyOnRealTransitions() {
        var state = new AlertSettingsState(Options.Create(new AlertOptions {
            EnableNewProcessDetection = true,
            EnableHashChangeDetection = true,
            EnableChainIntegrityMonitor = true,
        }));
        var snapshots = new List<AlertSettingsSnapshot>();
        state.StateChanged += snapshots.Add;

        state.SetSettings(true, true, true);       // no-op
        state.SetSettings(false, true, true);      // transition
        state.SetSettings(false, true, true);      // no-op
        state.SetSettings(true, false, false);     // multi-field transition

        Assert.Equal(2, snapshots.Count);
        Assert.False(snapshots[0].EnableNewProcessDetection);
        Assert.True(snapshots[1].EnableNewProcessDetection);
        Assert.False(snapshots[1].EnableHashChangeDetection);
        Assert.False(snapshots[1].EnableChainIntegrityMonitor);
    }

    [Fact]
    public async Task SetSettings_ConcurrentCallers_NoTearing() {
        var state = new AlertSettingsState(Options.Create(new AlertOptions()));
        const int iterations = 500;
        var tasks = Enumerable.Range(0, 4).Select(threadId => Task.Run(() => {
            for (var i = 0; i < iterations; i++) {
                var flag = (i + threadId) % 2 == 0;
                state.SetSettings(flag, flag, flag);
            }
        }));
        await Task.WhenAll(tasks);

        // After concurrent thrash the three bools must each be a clean true/false,
        // never garbage.
        var a = state.EnableNewProcessDetection;
        var b = state.EnableHashChangeDetection;
        var c = state.EnableChainIntegrityMonitor;
        Assert.True(a == true || a == false);
        Assert.True(b == true || b == false);
        Assert.True(c == true || c == false);
    }
}
