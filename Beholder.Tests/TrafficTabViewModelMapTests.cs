using System.Threading;
using System.Threading.Tasks;
using Beholder.Protocol.Local;
using Beholder.Tests.TestDoubles;
using Beholder.Ui.Models;
using Beholder.Ui.Services;
using Beholder.Ui.ViewModels;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beholder.Tests;

/// <summary>
/// Phase 8 MAP sub-view tests. Verifies that switching ViewMode to Map
/// triggers a country-breakdown fetch, that time-range and process changes
/// refetch only while Map is active, and that the LAN ("--") + Unknown
/// ("??") sentinel countries surface in the caption rather than on the
/// map. Mirrors the TrafficTabViewModelTests CreateViewModel pattern.
/// </summary>
public sealed class TrafficTabViewModelMapTests {
    private static (TrafficTabViewModel Vm, FakeDaemonClient Client) CreateVm() {
        var fakeClient = new FakeDaemonClient();
        var subscriber = new DaemonStreamSubscriber(
            fakeClient, TimeProvider.System,
            NullLogger<DaemonStreamSubscriber>.Instance);
        var service = new ProcessStateService(subscriber, fakeClient, TimeProvider.System);
        var loader = new HistoricalChartLoader(fakeClient);
        var vm = new TrafficTabViewModel(fakeClient, service, loader, new SyncDispatcher(), new TotalsExclusionUiState(), new FakeShellOpener(), new FakeClipboardWriter());
        return (vm, fakeClient);
    }

    private static GetCountryBreakdownResponse MakeBreakdown(
        params (string Country, long In, long Out)[] rows
    ) {
        var resp = new GetCountryBreakdownResponse();
        foreach (var (country, bin, bout) in rows) {
            resp.Countries.Add(new CountryTrafficSummary {
                Country = country,
                TotalBytesIn = bin,
                TotalBytesOut = bout,
            });
        }
        return resp;
    }

    [Fact]
    public async Task SetMapView_FromGraph_FlipsViewMode() {
        var (vm, _) = CreateVm();
        Assert.False(vm.IsMapActive);

        vm.SetMapViewCommand.Execute(null);

        // The OnViewModeChanged handler kicks off a fire-and-forget fetch;
        // let the SyncDispatcher drain so the command's side effects settle
        // before assert. Yielding once is enough — the fake's responder
        // returns synchronously.
        await Task.Yield();

        Assert.True(vm.IsMapActive);
        Assert.False(vm.IsGraphActive);
        Assert.False(vm.IsColsActive);
    }

    [Fact]
    public async Task OnViewModeChanged_ToMap_TriggersCountryBreakdownFetch() {
        var (vm, client) = CreateVm();
        var calls = 0;
        client.CountryBreakdownResponder = req => {
            calls++;
            return MakeBreakdown(("US", 1000, 500));
        };

        vm.SetMapViewCommand.Execute(null);
        await Task.Yield();

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ApplyMapBreakdown_LocalAndUnknownCountries_SurfaceInCaptionNotMap() {
        // The "--" (LAN) and "??" (Unknown) sentinels have no geographic
        // location so they belong in the caption strip, not on the map.
        var (vm, client) = CreateVm();
        client.CountryBreakdownResponder = req => MakeBreakdown(
            ("US", 1_000_000, 500_000),
            ("--", 200_000, 100_000),       // LAN
            ("??", 50_000, 25_000),         // Unknown
            ("DE", 2_000_000, 800_000));

        vm.SetMapViewCommand.Execute(null);
        await Task.Yield();

        // Only US + DE end up on the map.
        Assert.NotNull(vm.MapCountries);
        Assert.Equal(2, vm.MapCountries!.Count);
        Assert.DoesNotContain(vm.MapCountries, c => c.Country.Value == "--");
        Assert.DoesNotContain(vm.MapCountries, c => c.Country.Value == "??");

        // Caption mentions both the LAN and Unknown totals.
        Assert.Contains("LAN", vm.LocalAndUnknownCaption);
        Assert.Contains("Unknown", vm.LocalAndUnknownCaption);
    }

    [Fact]
    public async Task MaxMapBytes_ComputedFromRealCountriesOnly() {
        // The normalization ceiling drives HeatmapPalette stop selection;
        // including the (often-huge) LAN total would over-cool every real
        // country. The VM excludes LAN/Unknown from MaxMapBytes.
        var (vm, client) = CreateVm();
        client.CountryBreakdownResponder = req => MakeBreakdown(
            ("--", 999_999_999, 999_999_999),   // huge LAN — not counted
            ("US", 1_000_000, 500_000),         // real country, total 1.5 MB
            ("DE", 2_000_000, 800_000));        // real country, total 2.8 MB

        vm.SetMapViewCommand.Execute(null);
        await Task.Yield();

        // MaxMapBytes equals DE's total (2.8 MB), NOT the LAN value.
        Assert.Equal(2_800_000, vm.MaxMapBytes);
    }

    [Fact]
    public async Task OnSelectedTimeRangeChanged_WhileMapActive_RefetchesBreakdown() {
        var (vm, client) = CreateVm();
        var calls = 0;
        client.CountryBreakdownResponder = req => {
            calls++;
            return MakeBreakdown(("US", 100, 50));
        };

        // Activate MAP — first fetch.
        vm.SetMapViewCommand.Execute(null);
        await Task.Yield();
        Assert.Equal(1, calls);

        // Time-range change while MAP is active — second fetch.
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last1Hour);
        await Task.Yield();

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OnSelectedTimeRangeChanged_WhileGraphActive_DoesNotFetchMap() {
        var (vm, client) = CreateVm();
        var calls = 0;
        client.CountryBreakdownResponder = req => {
            calls++;
            return MakeBreakdown();
        };
        // VM starts in Graph mode — time-range changes should NOT trigger
        // country-breakdown fetches.
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last1Hour);
        await Task.Yield();

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RefreshMapAsync_RpcFailure_SurfacesInErrorBanner() {
        var (vm, client) = CreateVm();
        client.CountryBreakdownException = new RpcException(
            new Status(StatusCode.Unavailable, "daemon down"));

        vm.SetMapViewCommand.Execute(null);
        await Task.Yield();

        Assert.True(vm.HasError);
        Assert.Contains("daemon down", vm.ErrorMessage);
    }

    // ---- Phase 8 polish: top-3 destinations per country on hover ----

    private static GetProcessDestinationsResponse MakeDestinations(
        params (string Address, string? Hostname, long In, long Out)[] rows
    ) {
        var resp = new GetProcessDestinationsResponse();
        foreach (var (addr, host, bin, bout) in rows) {
            resp.Destinations.Add(new DestinationSummary {
                RemoteAddress = addr,
                Hostname = host ?? string.Empty,
                Country = "US",
                TotalBytesIn = bin,
                TotalBytesOut = bout,
                ConnectionCount = 1,
            });
        }
        return resp;
    }

    [Fact]
    public async Task OnHoveredCountryChanged_FirstHover_FetchesAndPopulatesDestinations() {
        var (vm, client) = CreateVm();
        client.ProcessDestinationsResponder = req => MakeDestinations(
            ("1.1.1.1", "github.com", 8_000_000, 200_000),
            ("2.2.2.2", "microsoft.com", 1_500_000, 500_000));

        vm.HoveredCountry = "US";
        await Task.Yield();

        Assert.NotNull(vm.HoveredCountryDestinations);
        Assert.Equal(2, vm.HoveredCountryDestinations!.Count);
        Assert.Equal("github.com", vm.HoveredCountryDestinations[0].Label);
        Assert.Equal(8_200_000, vm.HoveredCountryDestinations[0].TotalBytes);
        Assert.False(vm.HoveredCountryDestinationsLoading);
        Assert.False(vm.HoveredCountryDestinationsEmpty);
        Assert.False(vm.HoveredCountryDestinationsFailed);
    }

    [Fact]
    public async Task OnHoveredCountryChanged_HostnameMissing_FallsBackToRawIP() {
        var (vm, client) = CreateVm();
        client.ProcessDestinationsResponder = req => MakeDestinations(
            ("52.94.230.45", Hostname: null, 100, 50));

        vm.HoveredCountry = "US";
        await Task.Yield();

        var only = Assert.Single(vm.HoveredCountryDestinations!);
        Assert.Equal("52.94.230.45", only.Label);
    }

    [Fact]
    public async Task OnHoveredCountryChanged_SecondHoverSameCountry_UsesCache_NoRefetch() {
        var (vm, client) = CreateVm();
        var fetchCount = 0;
        client.ProcessDestinationsResponder = req => {
            fetchCount++;
            return MakeDestinations(("1.1.1.1", "example.com", 100, 50));
        };

        vm.HoveredCountry = "US";
        await Task.Yield();
        Assert.Equal(1, fetchCount);

        // Hover away then back to the same country — cache must serve
        // the second hover with no RPC.
        vm.HoveredCountry = null;
        vm.HoveredCountry = "US";
        await Task.Yield();

        Assert.Equal(1, fetchCount);
        Assert.NotNull(vm.HoveredCountryDestinations);
        Assert.Single(vm.HoveredCountryDestinations!);
    }

    [Fact]
    public async Task OnHoveredCountryChanged_NullValue_ClearsState() {
        var (vm, client) = CreateVm();
        client.ProcessDestinationsResponder = req => MakeDestinations(
            ("1.1.1.1", "example.com", 100, 50));
        vm.HoveredCountry = "US";
        await Task.Yield();
        Assert.NotNull(vm.HoveredCountryDestinations);

        vm.HoveredCountry = null;

        Assert.Null(vm.HoveredCountryDestinations);
        Assert.False(vm.HoveredCountryDestinationsLoading);
        Assert.False(vm.HoveredCountryDestinationsEmpty);
        Assert.False(vm.HoveredCountryDestinationsFailed);
    }

    [Fact]
    public async Task OnHoveredCountryChanged_EmptyResponse_SetsEmptyFlag() {
        var (vm, client) = CreateVm();
        client.ProcessDestinationsResponder = req => new GetProcessDestinationsResponse();

        vm.HoveredCountry = "US";
        await Task.Yield();

        Assert.NotNull(vm.HoveredCountryDestinations);
        Assert.Empty(vm.HoveredCountryDestinations!);
        Assert.True(vm.HoveredCountryDestinationsEmpty);
        Assert.False(vm.HoveredCountryDestinationsLoading);
        Assert.False(vm.HoveredCountryDestinationsFailed);
    }

    [Fact]
    public async Task OnHoveredCountryChanged_RpcFails_SetsFailedFlagAndNoErrorBanner() {
        // Locks in the silent-degrade design decision: destinations are
        // opportunistic augmentation, the country name + bytes header is
        // still useful, so an ErrorBanner that obscures the map is the
        // wrong response. See plan §"Approach" tooltip-states section
        // and PRINCIPLES.md §Error Handling.
        var (vm, client) = CreateVm();
        client.ProcessDestinationsException = new RpcException(
            new Status(StatusCode.Unavailable, "daemon down"));

        vm.HoveredCountry = "US";
        await Task.Yield();

        Assert.True(vm.HoveredCountryDestinationsFailed);
        Assert.False(vm.HoveredCountryDestinationsLoading);
        Assert.Null(vm.HoveredCountryDestinations);
        // Critical: no global ErrorBanner.
        Assert.False(vm.HasError);
        Assert.Empty(vm.ErrorMessage);
    }

    [Fact]
    public async Task OnSelectedTimeRangeChanged_ClearsTopDestCache_NextHoverRefetches() {
        var (vm, client) = CreateVm();
        var fetchCount = 0;
        client.ProcessDestinationsResponder = req => {
            fetchCount++;
            return MakeDestinations(("1.1.1.1", "example.com", 100, 50));
        };

        // First hover populates the cache.
        vm.HoveredCountry = "US";
        await Task.Yield();
        Assert.Equal(1, fetchCount);

        // Time-range change should invalidate the cache; a fresh hover
        // to the same country must re-fetch.
        vm.HoveredCountry = null;
        vm.SelectedTimeRange = TimeRangeSelection.FromPreset(TimeRangePreset.Last1Hour);
        vm.HoveredCountry = "US";
        await Task.Yield();

        Assert.Equal(2, fetchCount);
    }
}
