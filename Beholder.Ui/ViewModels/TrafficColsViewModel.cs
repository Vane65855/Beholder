using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Beholder.Protocol.Local;
using Beholder.Ui.Helpers;
using Beholder.Ui.Models;
using Beholder.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Backs the Traffic tab's COLS sub-view — a 3-column destination /
/// protocol / country breakdown of the current range (and process
/// selection). Each column is an <see cref="ObservableCollection{T}"/> of
/// <c>{name, bar ratio, bytes label}</c> rows sorted by total bytes desc.
/// </summary>
/// <remarks>
/// One in-flight query at a time — rapid range/process changes cancel the
/// previous <c>RefreshAsync</c> via an owned <see cref="CancellationTokenSource"/>.
/// The three RPCs fire in parallel and collections are populated only once
/// all three complete, so the UI never shows two of three columns filled.
/// </remarks>
internal sealed partial class TrafficColsViewModel : ViewModelBase, IDisposable {
    private readonly IDaemonClient _daemonClient;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public ObservableCollection<HostRow> Hosts { get; } = [];
    public ObservableCollection<ProtocolRow> Protocols { get; } = [];
    public ObservableCollection<CountryRow> Countries { get; } = [];

    public TrafficColsViewModel(IDaemonClient daemonClient) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        _daemonClient = daemonClient;
    }

    /// <summary>
    /// Refreshes all three columns for the given range and process selection.
    /// <paramref name="processPath"/> is <c>null</c> for the "All processes"
    /// aggregate. Any prior in-flight refresh is cancelled. Callers must
    /// invoke from the UI thread (the ObservableCollections bind directly to
    /// the view).
    /// </summary>
    public async Task RefreshAsync(TimeRangeSelection range, string? processPath) {
        ArgumentNullException.ThrowIfNull(range);

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;

        try {
            var fromNs = range.From.ToUnixTimeMilliseconds() * 1_000_000L;
            var toNs = range.To.ToUnixTimeMilliseconds() * 1_000_000L;
            var requestProcessPath = processPath ?? string.Empty;

            var destinationsTask = _daemonClient.GetProcessDestinationsAsync(
                new GetProcessDestinationsRequest {
                    ProcessPath = requestProcessPath,
                    FromUnixNs = fromNs,
                    ToUnixNs = toNs,
                }, ct);
            var protocolsTask = _daemonClient.GetProtocolBreakdownAsync(
                new GetProtocolBreakdownRequest {
                    ProcessPath = requestProcessPath,
                    FromUnixNs = fromNs,
                    ToUnixNs = toNs,
                }, ct);
            var countriesTask = _daemonClient.GetCountryBreakdownAsync(
                new GetCountryBreakdownRequest {
                    ProcessPath = requestProcessPath,
                    FromUnixNs = fromNs,
                    ToUnixNs = toNs,
                }, ct);

            await Task.WhenAll(destinationsTask, protocolsTask, countriesTask).ConfigureAwait(true);

            if (ct.IsCancellationRequested) return;

            PopulateHosts(destinationsTask.Result.Destinations);
            PopulateProtocols(protocolsTask.Result.Protocols);
            PopulateCountries(countriesTask.Result.Countries);

            IsEmpty = Hosts.Count == 0 && Protocols.Count == 0 && Countries.Count == 0;
            IsLoading = false;
        } catch (OperationCanceledException) {
            // User moved on — the superseding refresh owns the UI state.
            throw;
        } catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) {
            // grpc-dotnet surfaces cancellation as RpcException.Cancelled.
            // Normalise so the caller has a single exception type for "moved on."
            throw new OperationCanceledException("Cancelled via gRPC status", ex);
        } catch (RpcException) {
            IsLoading = false;
            HasError = true;
            ErrorMessage = "Failed to load column data.";
        }
    }

    public void Dispose() {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Clears the error banner. Bound to the close-X on the inline
    /// <see cref="Beholder.Ui.Controls.ErrorBanner"/>. See UI_DESIGN.md §5.10.
    /// </summary>
    [RelayCommand]
    private void DismissError() {
        HasError = false;
        ErrorMessage = string.Empty;
    }

    private void PopulateHosts(IReadOnlyList<DestinationSummary> destinations) {
        Hosts.Clear();
        var max = 0L;
        foreach (var d in destinations) {
            var total = d.TotalBytesIn + d.TotalBytesOut;
            if (total > max) max = total;
        }
        foreach (var d in destinations) {
            var total = d.TotalBytesIn + d.TotalBytesOut;
            var display = string.IsNullOrWhiteSpace(d.Hostname) ? d.RemoteAddress : d.Hostname;
            Hosts.Add(new HostRow(
                Display: display,
                Country: d.Country,
                TotalBytes: total,
                BytesLabel: ByteFormatter.FormatBytes(total),
                BarRatio: max == 0 ? 0 : (double)total / max));
        }
    }

    private void PopulateProtocols(IReadOnlyList<ProtocolBreakdownSummary> protocols) {
        Protocols.Clear();
        var max = 0L;
        foreach (var p in protocols) {
            var total = p.TotalBytesIn + p.TotalBytesOut;
            if (total > max) max = total;
        }
        foreach (var p in protocols) {
            var total = p.TotalBytesIn + p.TotalBytesOut;
            Protocols.Add(new ProtocolRow(
                Name: p.ProtocolName,
                Transport: p.Transport,
                TotalBytes: total,
                BytesLabel: ByteFormatter.FormatBytes(total),
                BarRatio: max == 0 ? 0 : (double)total / max));
        }
    }

    private void PopulateCountries(IReadOnlyList<CountryTrafficSummary> countries) {
        Countries.Clear();
        var max = 0L;
        foreach (var c in countries) {
            var total = c.TotalBytesIn + c.TotalBytesOut;
            if (total > max) max = total;
        }
        foreach (var c in countries) {
            var total = c.TotalBytesIn + c.TotalBytesOut;
            Countries.Add(new CountryRow(
                Alpha2: c.Country,
                Display: DisplayCountry(c.Country),
                TotalBytes: total,
                BytesLabel: ByteFormatter.FormatBytes(total),
                BarRatio: max == 0 ? 0 : (double)total / max));
        }
    }

    private static string DisplayCountry(string alpha2) => alpha2 switch {
        "--" => "Local",
        "??" => "Unknown",
        _ => alpha2,
    };
}
