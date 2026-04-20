using Beholder.Protocol.Local;
using Beholder.Ui.Models;
using Beholder.Ui.ViewModels;
using Grpc.Core;

namespace Beholder.Tests;

public class TrafficColsViewModelTests {
    private static TrafficColsViewModel CreateViewModel(FakeDaemonClient fakeClient) {
        return new TrafficColsViewModel(fakeClient);
    }

    private static TimeRangeSelection Range() =>
        TimeRangeSelection.FromPreset(TimeRangePreset.Last1Hour);

    [Fact]
    public async Task RefreshAsync_PopulatesAllThreeCollections() {
        var fakeClient = new FakeDaemonClient();
        fakeClient.ProcessDestinationsResponder = _ => {
            var response = new GetProcessDestinationsResponse();
            response.Destinations.Add(new DestinationSummary {
                RemoteAddress = "1.1.1.1",
                Hostname = "one.example.com",
                Country = "US",
                TotalBytesIn = 100,
                TotalBytesOut = 50,
                ConnectionCount = 1,
            });
            return response;
        };
        fakeClient.ProtocolBreakdownResponder = _ => {
            var response = new GetProtocolBreakdownResponse();
            response.Protocols.Add(new ProtocolBreakdownSummary {
                ProtocolName = "HTTPS", Transport = "TCP",
                TotalBytesIn = 200, TotalBytesOut = 100,
            });
            return response;
        };
        fakeClient.CountryBreakdownResponder = _ => {
            var response = new GetCountryBreakdownResponse();
            response.Countries.Add(new CountryTrafficSummary {
                Country = "US", TotalBytesIn = 300, TotalBytesOut = 150,
            });
            return response;
        };

        var vm = CreateViewModel(fakeClient);
        await vm.RefreshAsync(Range(), processPath: null);

        Assert.Single(vm.Hosts);
        Assert.Single(vm.Protocols);
        Assert.Single(vm.Countries);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsEmpty);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task RefreshAsync_ComputesBarRatiosFromMaxInColumn() {
        // Largest row in each column has BarRatio=1.0; others are fractions.
        // Max for Hosts is 90 (30+60), so the 30-byte row should have ratio 1/3.
        var fakeClient = new FakeDaemonClient();
        fakeClient.ProcessDestinationsResponder = _ => {
            var response = new GetProcessDestinationsResponse();
            response.Destinations.Add(new DestinationSummary {
                RemoteAddress = "big.example.com", Hostname = "big.example.com",
                Country = "US", TotalBytesIn = 60, TotalBytesOut = 30, ConnectionCount = 1,
            });
            response.Destinations.Add(new DestinationSummary {
                RemoteAddress = "small.example.com", Hostname = "small.example.com",
                Country = "US", TotalBytesIn = 20, TotalBytesOut = 10, ConnectionCount = 1,
            });
            return response;
        };
        var vm = CreateViewModel(fakeClient);

        await vm.RefreshAsync(Range(), processPath: null);

        Assert.Equal(2, vm.Hosts.Count);
        Assert.Equal(1.0, vm.Hosts[0].BarRatio, precision: 6);
        Assert.Equal(30.0 / 90.0, vm.Hosts[1].BarRatio, precision: 6);
        // EmptyRatio complements BarRatio so the grid sums to 1.0.
        Assert.Equal(0.0, vm.Hosts[0].EmptyRatio, precision: 6);
        Assert.Equal(1.0 - (30.0 / 90.0), vm.Hosts[1].EmptyRatio, precision: 6);
    }

    [Fact]
    public async Task RefreshAsync_EmptyResponse_SetsIsEmpty() {
        var fakeClient = new FakeDaemonClient();
        // No responders set — FakeDaemonClient returns empty responses by default.
        var vm = CreateViewModel(fakeClient);

        await vm.RefreshAsync(Range(), processPath: null);

        Assert.Empty(vm.Hosts);
        Assert.Empty(vm.Protocols);
        Assert.Empty(vm.Countries);
        Assert.True(vm.IsEmpty);
        Assert.False(vm.IsLoading);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task RefreshAsync_RpcError_SetsHasError() {
        var fakeClient = new FakeDaemonClient();
        fakeClient.CountryBreakdownException = new RpcException(
            new Status(StatusCode.Internal, "daemon offline"));
        var vm = CreateViewModel(fakeClient);

        await vm.RefreshAsync(Range(), processPath: null);

        Assert.True(vm.HasError);
        Assert.Equal("Failed to load column data.", vm.ErrorMessage);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task RefreshAsync_CancellationPropagates() {
        // Superseding refresh cancels prior in-flight work. grpc-dotnet would
        // surface it as RpcException.Cancelled — VM normalises to OCE so the
        // caller has a single "moved on" signal.
        var fakeClient = new FakeDaemonClient();
        fakeClient.CountryBreakdownException = new RpcException(
            new Status(StatusCode.Cancelled, "cancelled"));
        var vm = CreateViewModel(fakeClient);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => vm.RefreshAsync(Range(), processPath: null));
    }

    [Fact]
    public async Task RefreshAsync_PassesProcessPathThrough() {
        // Per-process mode: each of the 3 RPCs must receive the caller's
        // process_path so the daemon scopes the query.
        var fakeClient = new FakeDaemonClient();
        string? observedProcessPath = null;
        fakeClient.ProcessDestinationsResponder = req => {
            observedProcessPath = req.ProcessPath;
            return new GetProcessDestinationsResponse();
        };
        var vm = CreateViewModel(fakeClient);

        await vm.RefreshAsync(Range(), processPath: "C:/app/firefox.exe");

        Assert.Equal("C:/app/firefox.exe", observedProcessPath);
    }

    [Fact]
    public async Task RefreshAsync_NullProcessPath_SendsEmptyString() {
        // proto3 has no nullable strings; "" = aggregate mode on the wire.
        var fakeClient = new FakeDaemonClient();
        string? observedProcessPath = null;
        fakeClient.ProcessDestinationsResponder = req => {
            observedProcessPath = req.ProcessPath;
            return new GetProcessDestinationsResponse();
        };
        var vm = CreateViewModel(fakeClient);

        await vm.RefreshAsync(Range(), processPath: null);

        Assert.Equal(string.Empty, observedProcessPath);
    }

    [Fact]
    public async Task RefreshAsync_CountrySentinels_MapToDisplayLabels() {
        // "??" → "Unknown", "--" → "Local". Keeps the UI label layer away
        // from sentinel-code oddities.
        var fakeClient = new FakeDaemonClient();
        fakeClient.CountryBreakdownResponder = _ => {
            var response = new GetCountryBreakdownResponse();
            response.Countries.Add(new CountryTrafficSummary {
                Country = "--", TotalBytesIn = 10, TotalBytesOut = 5,
            });
            response.Countries.Add(new CountryTrafficSummary {
                Country = "??", TotalBytesIn = 20, TotalBytesOut = 10,
            });
            response.Countries.Add(new CountryTrafficSummary {
                Country = "US", TotalBytesIn = 30, TotalBytesOut = 15,
            });
            return response;
        };
        var vm = CreateViewModel(fakeClient);

        await vm.RefreshAsync(Range(), processPath: null);

        Assert.Equal(3, vm.Countries.Count);
        Assert.Contains(vm.Countries, c => c.Display == "Local");
        Assert.Contains(vm.Countries, c => c.Display == "Unknown");
        Assert.Contains(vm.Countries, c => c.Display == "US");
    }
}
