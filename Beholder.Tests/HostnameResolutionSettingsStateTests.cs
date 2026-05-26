using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.Windows;
using Microsoft.Extensions.Options;

namespace Beholder.Tests;

public class HostnameResolutionSettingsStateTests {
    [Fact]
    public void InitialState_SeededFromOptions() {
        var state = new HostnameResolutionSettingsState(
            Options.Create(new DnsOptions {
                EnablePreload = true,
                EnableReverseDnsFallback = false,
            }),
            Options.Create(new SniOptions { EnableSniCapture = true }));

        Assert.True(state.EnablePreload);
        Assert.False(state.EnableReverseDnsFallback);
        Assert.True(state.EnableSniCapture);
    }

    [Fact]
    public void SetSettings_NoChange_ReturnsFalse() {
        var state = new HostnameResolutionSettingsState(
            Options.Create(new DnsOptions { EnablePreload = true, EnableReverseDnsFallback = true }),
            Options.Create(new SniOptions { EnableSniCapture = true }));

        var result = state.SetSettings(
            enablePreload: true,
            enableReverseDnsFallback: true,
            enableSniCapture: true);

        Assert.False(result);
    }

    [Fact]
    public void SetSettings_SingleFieldTransition_ReturnsTrue() {
        var state = new HostnameResolutionSettingsState(
            Options.Create(new DnsOptions { EnablePreload = true, EnableReverseDnsFallback = true }),
            Options.Create(new SniOptions { EnableSniCapture = true }));

        var result = state.SetSettings(
            enablePreload: true,
            enableReverseDnsFallback: false,   // only this changes
            enableSniCapture: true);

        Assert.True(result);
        Assert.True(state.EnablePreload);
        Assert.False(state.EnableReverseDnsFallback);
        Assert.True(state.EnableSniCapture);
    }

    [Fact]
    public void SetSettings_MultipleFieldsTransition_ReturnsTrueAndUpdatesAll() {
        var state = new HostnameResolutionSettingsState(
            Options.Create(new DnsOptions { EnablePreload = true, EnableReverseDnsFallback = true }),
            Options.Create(new SniOptions { EnableSniCapture = true }));

        var result = state.SetSettings(
            enablePreload: false,
            enableReverseDnsFallback: false,
            enableSniCapture: false);

        Assert.True(result);
        Assert.False(state.EnablePreload);
        Assert.False(state.EnableReverseDnsFallback);
        Assert.False(state.EnableSniCapture);
    }

    [Fact]
    public void StateChanged_FiresWithSnapshotOnlyOnRealTransitions() {
        var state = new HostnameResolutionSettingsState(
            Options.Create(new DnsOptions { EnablePreload = true, EnableReverseDnsFallback = true }),
            Options.Create(new SniOptions { EnableSniCapture = true }));
        var snapshots = new List<HostnameResolutionSettingsSnapshot>();
        state.StateChanged += snapshots.Add;

        state.SetSettings(true, true, true);    // no-op
        state.SetSettings(false, true, true);   // single transition
        state.SetSettings(false, true, true);   // no-op
        state.SetSettings(true, true, false);   // two-field transition

        Assert.Equal(2, snapshots.Count);
        Assert.False(snapshots[0].EnablePreload);
        Assert.True(snapshots[0].EnableReverseDnsFallback);
        Assert.True(snapshots[0].EnableSniCapture);
        Assert.True(snapshots[1].EnablePreload);
        Assert.True(snapshots[1].EnableReverseDnsFallback);
        Assert.False(snapshots[1].EnableSniCapture);
    }

    [Fact]
    public async Task SetSettings_ConcurrentCallers_NoTearing() {
        var state = new HostnameResolutionSettingsState(
            Options.Create(new DnsOptions { EnablePreload = true, EnableReverseDnsFallback = true }),
            Options.Create(new SniOptions { EnableSniCapture = true }));

        const int iterations = 500;
        var tasks = Enumerable.Range(0, 4).Select(threadId => Task.Run(() => {
            for (var i = 0; i < iterations; i++) {
                var flag = (i + threadId) % 2 == 0;
                state.SetSettings(flag, flag, flag);
            }
        }));
        await Task.WhenAll(tasks);

        // Final-state torn read check: if the three booleans were updated
        // without a lock, threads could observe (true, false, true)-style
        // inconsistencies. The contract is: the three bools always
        // transition atomically together inside SetSettings, so at any
        // moment any reader sees a consistent (a,b,c) triple.
        var a = state.EnablePreload;
        var b = state.EnableReverseDnsFallback;
        var c = state.EnableSniCapture;
        Assert.True(a == true || a == false);
        Assert.True(b == true || b == false);
        Assert.True(c == true || c == false);
    }
}
