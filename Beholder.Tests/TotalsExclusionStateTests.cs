using Beholder.Core;
using Beholder.Daemon;

namespace Beholder.Tests;

/// <summary>
/// Covers the Traffic Totals exclusion state singleton: empty seed,
/// case-insensitive membership, order-insensitive change detection, and the
/// fire-only-on-real-transition event contract shared by all Settings states.
/// </summary>
public class TotalsExclusionStateTests {
    [Fact]
    public void Ctor_SeedsEmpty() {
        var state = new TotalsExclusionState();

        Assert.Empty(state.ExcludedProcessPaths);
        Assert.False(state.IsExcluded(@"C:\any.exe"));
    }

    [Fact]
    public void SetExcludedPaths_RealTransition_ReturnsTrueAndFiresEvent() {
        var state = new TotalsExclusionState();
        TotalsExclusionSnapshot? observed = null;
        state.StateChanged += snapshot => observed = snapshot;

        var changed = state.SetExcludedPaths([@"C:\vpn\wireguard.exe"]);

        Assert.True(changed);
        Assert.NotNull(observed);
        Assert.Equal([@"C:\vpn\wireguard.exe"], observed!.ExcludedProcessPaths);
        Assert.Equal([@"C:\vpn\wireguard.exe"], state.ExcludedProcessPaths);
    }

    [Fact]
    public void IsExcluded_MatchesCaseInsensitively() {
        var state = new TotalsExclusionState();
        state.SetExcludedPaths([@"C:\VPN\WireGuard.exe"]);

        Assert.True(state.IsExcluded(@"c:\vpn\wireguard.exe"));
        Assert.False(state.IsExcluded(@"c:\vpn\other.exe"));
    }

    [Fact]
    public void IsExcluded_NullOrEmpty_ReturnsFalse() {
        var state = new TotalsExclusionState();
        state.SetExcludedPaths([@"C:\vpn\wireguard.exe"]);

        Assert.False(state.IsExcluded(""));
        Assert.False(state.IsExcluded(null!));
    }

    [Fact]
    public void SetExcludedPaths_SameSetDifferentOrderAndCase_IsNoOp() {
        var state = new TotalsExclusionState();
        state.SetExcludedPaths([@"C:\a.exe", @"C:\b.exe"]);
        var fired = false;
        state.StateChanged += _ => fired = true;

        var changed = state.SetExcludedPaths([@"C:\B.EXE", @"C:\A.EXE"]);

        Assert.False(changed);
        Assert.False(fired);
    }

    [Fact]
    public void SetExcludedPaths_EmptyList_ClearsExclusions() {
        var state = new TotalsExclusionState();
        state.SetExcludedPaths([@"C:\a.exe"]);

        var changed = state.SetExcludedPaths([]);

        Assert.True(changed);
        Assert.Empty(state.ExcludedProcessPaths);
        Assert.False(state.IsExcluded(@"C:\a.exe"));
    }
}
