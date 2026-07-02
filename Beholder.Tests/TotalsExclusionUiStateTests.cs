using Beholder.Protocol.Local;
using Beholder.Tests.TestDoubles;
using Beholder.Ui.Services;

namespace Beholder.Tests;

/// <summary>
/// Covers the UI-side mirror of the "Exclude from totals" list: change-only
/// eventing, case-insensitive membership, the show-excluded preference, and
/// the best-effort connect-time seed from GetSettings.
/// </summary>
public class TotalsExclusionUiStateTests {
    [Fact]
    public void SetExcludedPaths_RealChange_FiresChanged() {
        var state = new TotalsExclusionUiState();
        var fired = 0;
        state.Changed += () => fired++;

        state.SetExcludedPaths([@"C:\vpn\wireguard.exe"]);

        Assert.Equal(1, fired);
        Assert.True(state.IsExcluded(@"c:\VPN\WIREGUARD.exe"));
        Assert.False(state.IsExcluded(@"c:\other.exe"));
    }

    [Fact]
    public void SetExcludedPaths_SameSet_DoesNotFire() {
        var state = new TotalsExclusionUiState();
        state.SetExcludedPaths([@"C:\a.exe"]);
        var fired = 0;
        state.Changed += () => fired++;

        state.SetExcludedPaths([@"C:\A.EXE"]);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void SetShowExcluded_FiresOnlyOnFlip() {
        var state = new TotalsExclusionUiState();
        var fired = 0;
        state.Changed += () => fired++;

        state.SetShowExcluded(true);
        state.SetShowExcluded(true);

        Assert.Equal(1, fired);
        Assert.True(state.ShowExcluded);
    }

    [Fact]
    public async Task RefreshFromDaemonAsync_SeedsListFromGetSettings() {
        var client = new FakeDaemonClient();
        var totals = new TotalsSettingsValues();
        totals.ExcludedProcessPaths.Add(@"C:\vpn\wireguard.exe");
        client.GetSettingsResponder = _ => new GetSettingsResponse { Totals = totals };
        var state = new TotalsExclusionUiState();

        await state.RefreshFromDaemonAsync(client, CancellationToken.None);

        Assert.Equal([@"C:\vpn\wireguard.exe"], state.ExcludedProcessPaths);
    }

    [Fact]
    public async Task RefreshFromDaemonAsync_RpcFails_KeepsCurrentListAndDoesNotThrow() {
        var client = new FakeDaemonClient { GetSettingsException = new InvalidOperationException("offline") };
        var state = new TotalsExclusionUiState();
        state.SetExcludedPaths([@"C:\a.exe"]);

        await state.RefreshFromDaemonAsync(client, CancellationToken.None);

        Assert.Equal([@"C:\a.exe"], state.ExcludedProcessPaths);
    }
}
