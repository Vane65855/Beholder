using Beholder.Core;
using Beholder.Daemon;
using Microsoft.Extensions.Options;

namespace Beholder.Tests;

public class ScannerSettingsStateTests {
    [Fact]
    public void InitialState_SeededFromOptions() {
        var state = new ScannerSettingsState(
            Options.Create(new ScannerOptions { EnableHostnameResolution = false }));

        Assert.False(state.EnableHostnameResolution);
    }

    [Fact]
    public void SetSettings_NoChange_ReturnsFalse() {
        var state = new ScannerSettingsState(
            Options.Create(new ScannerOptions { EnableHostnameResolution = true }));

        var result = state.SetSettings(true);

        Assert.False(result);
        Assert.True(state.EnableHostnameResolution);
    }

    [Fact]
    public void SetSettings_RealTransition_ReturnsTrueAndUpdatesValue() {
        var state = new ScannerSettingsState(
            Options.Create(new ScannerOptions { EnableHostnameResolution = true }));

        var result = state.SetSettings(false);

        Assert.True(result);
        Assert.False(state.EnableHostnameResolution);
    }

    [Fact]
    public void StateChanged_FiresOnlyOnRealTransitions() {
        var state = new ScannerSettingsState(
            Options.Create(new ScannerOptions { EnableHostnameResolution = true }));
        var snapshots = new List<ScannerSettingsSnapshot>();
        state.StateChanged += snapshots.Add;

        state.SetSettings(true);    // no-op
        state.SetSettings(false);   // transition
        state.SetSettings(false);   // no-op
        state.SetSettings(true);    // transition

        Assert.Equal(2, snapshots.Count);
        Assert.False(snapshots[0].EnableHostnameResolution);
        Assert.True(snapshots[1].EnableHostnameResolution);
    }
}
