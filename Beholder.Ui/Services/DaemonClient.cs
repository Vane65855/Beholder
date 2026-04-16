using System;
using System.Threading;
using System.Threading.Tasks;
using Beholder.Protocol.Local;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace Beholder.Ui.Services;

internal sealed class DaemonClient : IDaemonClient {
    private static readonly TimeSpan[] BackoffDelays = [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15),
    ];

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DaemonClient> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();

    private GrpcChannel? _channel;
    private BeholderLocal.BeholderLocalClient? _client;
    private ConnectionState _state = ConnectionState.Disconnected;

    public ConnectionState State => _state;
    public DaemonStatusInfo StatusInfo => DaemonStatusInfo.FromState(_state);
    public event Action<DaemonStatusInfo>? StateChanged;

    public DaemonClient(TimeProvider timeProvider, ILogger<DaemonClient> logger) {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        var token = linked.Token;

        // Reconnection runs indefinitely with exponential backoff (cap 15s).
        // We never transition to a permanent "offline" state — the user explicitly
        // stopping the daemon is the dominant scenario in Phase 5, so the UI's
        // job is to be ready to reconnect the moment the daemon returns. This
        // will be revisited when the daemon becomes a Windows Service in Phase 11.
        var attempt = 0;
        while (!token.IsCancellationRequested) {
            SetState(attempt == 0 ? ConnectionState.Connecting : ConnectionState.Reconnecting);

            try {
                _channel?.Dispose();
                _channel = GrpcChannel.ForAddress("http://127.0.0.1:50051");
                _client = new BeholderLocal.BeholderLocalClient(_channel);

                // Health probe — if the daemon is unreachable this throws
                await _client.GetSnapshotAsync(new GetSnapshotRequest(), cancellationToken: token);

                SetState(ConnectionState.Connected);
                _logger.LogInformation("Connected to daemon");
                attempt = 0;

                // Stay connected — poll periodically to detect disconnection
                await MonitorConnection(token);
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                break;
            } catch (RpcException ex) {
                _logger.LogWarning("Daemon connection failed: {Status}", ex.StatusCode);
            } catch (Exception ex) {
                _logger.LogWarning("Daemon connection failed: {Message}", ex.Message);
            }

            SetState(ConnectionState.Reconnecting);
            var delay = BackoffDelays[Math.Min(attempt, BackoffDelays.Length - 1)];
            attempt++;

            try {
                await Task.Delay(delay, _timeProvider, token);
            } catch (OperationCanceledException) {
                break;
            }
        }

        SetState(ConnectionState.Disconnected);
    }

    private async Task MonitorConnection(CancellationToken ct) {
        // Poll every 5 seconds to detect if daemon goes away
        while (!ct.IsCancellationRequested) {
            await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, ct);

            try {
                await _client!.GetSnapshotAsync(new GetSnapshotRequest(), cancellationToken: ct);
            } catch (OperationCanceledException) {
                throw;
            } catch {
                _logger.LogWarning("Lost connection to daemon");
                return;
            }
        }
    }

    public async Task<GetSnapshotResponse> GetSnapshotAsync(CancellationToken ct) {
        EnsureConnected();
        return await _client!.GetSnapshotAsync(new GetSnapshotRequest(), cancellationToken: ct);
    }

    public async Task<ApplyFirewallRuleResponse> ApplyFirewallRuleAsync(
        ApplyFirewallRuleRequest request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();
        return await _client!.ApplyFirewallRuleAsync(request, cancellationToken: ct);
    }

    public async Task<MarkAlertReadResponse> MarkAlertReadAsync(
        MarkAlertReadRequest request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();
        return await _client!.MarkAlertReadAsync(request, cancellationToken: ct);
    }

    public async Task<VerifyChainResponse> VerifyChainAsync(
        VerifyChainRequest request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();
        return await _client!.VerifyChainAsync(request, cancellationToken: ct);
    }

    public async Task<GetProcessTimelineResponse> GetProcessTimelineAsync(
        GetProcessTimelineRequest request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();
        return await _client!.GetProcessTimelineAsync(request, cancellationToken: ct);
    }

    public async Task<GetAggregateTimelineResponse> GetAggregateTimelineAsync(
        GetAggregateTimelineRequest request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();
        return await _client!.GetAggregateTimelineAsync(request, cancellationToken: ct);
    }

    public async Task<GetProcessDestinationsResponse> GetProcessDestinationsAsync(
        GetProcessDestinationsRequest request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();
        return await _client!.GetProcessDestinationsAsync(request, cancellationToken: ct);
    }

    public async Task<GetCountryBreakdownResponse> GetCountryBreakdownAsync(
        GetCountryBreakdownRequest request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();
        return await _client!.GetCountryBreakdownAsync(request, cancellationToken: ct);
    }

    public async Task<GetProcessSummariesResponse> GetProcessSummariesAsync(
        GetProcessSummariesRequest request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        EnsureConnected();
        return await _client!.GetProcessSummariesAsync(request, cancellationToken: ct);
    }

    public AsyncServerStreamingCall<DaemonEvent> Subscribe(CancellationToken ct) {
        EnsureConnected();
        return _client!.Subscribe(new SubscribeRequest(), cancellationToken: ct);
    }

    public async ValueTask DisposeAsync() {
        await _shutdownCts.CancelAsync();
        _channel?.Dispose();
        _shutdownCts.Dispose();
    }

    private void SetState(ConnectionState newState) {
        if (_state == newState) return;
        _state = newState;
        StateChanged?.Invoke(StatusInfo);
    }

    private void EnsureConnected() {
        if (_state != ConnectionState.Connected)
            throw new InvalidOperationException("Not connected to daemon");
    }
}
