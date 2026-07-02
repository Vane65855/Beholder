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

    // Task of the running ConnectLoopAsync. Stored so DisposeAsync can await
    // the loop's unwind before disposing the channel — otherwise the loop's
    // in-flight health probe / MonitorConnection RPC may see a just-disposed
    // channel and surface ObjectDisposedException during shutdown.
    private Task? _connectTask;

    public ConnectionState State => _state;
    public DaemonStatusInfo StatusInfo => DaemonStatusInfo.FromState(_state);
    public event Action<DaemonStatusInfo>? StateChanged;

    public DaemonClient(TimeProvider timeProvider, ILogger<DaemonClient> logger) {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken cancellationToken) {
        if (_connectTask is not null)
            throw new InvalidOperationException("ConnectAsync has already been started.");
        _connectTask = ConnectLoopAsync(cancellationToken);
        return _connectTask;
    }

    // ADR 014: the daemon serves the control RPC over a DACL'd named pipe
    // (\\.\pipe\beholder), not a TCP socket any local process could reach.
    // Connect via a SocketsHttpHandler whose ConnectCallback opens the pipe; the
    // channel address is a placeholder since the handler does the real connect.
    // The 64 MB receive cap matches the daemon's send cap (Phase 11.3 export).
    private static GrpcChannel CreateChannel() {
        const int MaxReceiveMessageBytes = 64 * 1024 * 1024;
#if PLATFORM_WINDOWS
        var handler = new System.Net.Http.SocketsHttpHandler {
            ConnectCallback = async (_, ct) => {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(
                    ".", Beholder.Protocol.IpcEndpoint.PipeName,
                    System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                try {
                    await pipe.ConnectAsync(ct).ConfigureAwait(false);
                } catch {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                return pipe;
            },
        };
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions {
            MaxReceiveMessageSize = MaxReceiveMessageBytes,
            HttpHandler = handler,
        });
#else
        // Non-Windows: the Linux daemon (a Unix-domain-socket listener) isn't
        // built yet; keep the TCP localhost path so a future Linux port works.
        return GrpcChannel.ForAddress("http://127.0.0.1:50051", new GrpcChannelOptions {
            MaxReceiveMessageSize = MaxReceiveMessageBytes,
        });
#endif
    }

    private async Task ConnectLoopAsync(CancellationToken cancellationToken) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var token = linked.Token;

        // Reconnection runs indefinitely with exponential backoff (cap 15s).
        // We never transition to a permanent "offline" state — the user explicitly
        // stopping the daemon is the dominant scenario in Phase 5, so the UI's
        // job is to be ready to reconnect the moment the daemon returns. This
        // will be revisited when the daemon becomes a Windows Service in Phase 12.
        var attempt = 0;
        while (!token.IsCancellationRequested) {
            SetState(attempt == 0 ? ConnectionState.Connecting : ConnectionState.Reconnecting);

            try {
                _channel?.Dispose();
                _channel = CreateChannel();
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

    private async Task MonitorConnection(CancellationToken cancellationToken) {
        // Poll every 5 seconds to detect if daemon goes away
        while (!cancellationToken.IsCancellationRequested) {
            await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, cancellationToken);

            try {
                var client = GetConnectedClient();
                await client.GetSnapshotAsync(new GetSnapshotRequest(), cancellationToken: cancellationToken);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                // Capture the exception reason for the log — otherwise an
                // unexpected type (e.g., NullReferenceException during channel
                // teardown) exits the monitor loop with no diagnostic.
                _logger.LogWarning("Lost connection to daemon: {Reason}", ex.Message);
                return;
            }
        }
    }

    public async Task<GetSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) {
        var client = GetConnectedClient();
        return await client.GetSnapshotAsync(new GetSnapshotRequest(), cancellationToken: cancellationToken);
    }

    public async Task<ApplyFirewallRuleResponse> ApplyFirewallRuleAsync(
        ApplyFirewallRuleRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.ApplyFirewallRuleAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<RemoveFirewallRuleResponse> RemoveFirewallRuleAsync(
        RemoveFirewallRuleRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.RemoveFirewallRuleAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<ListFirewallRulesResponse> ListFirewallRulesAsync(
        ListFirewallRulesRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.ListFirewallRulesAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<SetFirewallEnabledResponse> SetFirewallEnabledAsync(
        SetFirewallEnabledRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.SetFirewallEnabledAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<MarkAlertReadResponse> MarkAlertReadAsync(
        MarkAlertReadRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.MarkAlertReadAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<VerifyChainResponse> VerifyChainAsync(
        VerifyChainRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.VerifyChainAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<ExportChainResponse> ExportChainAsync(
        ExportChainRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.ExportChainAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<GetProcessTimelineResponse> GetProcessTimelineAsync(
        GetProcessTimelineRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.GetProcessTimelineAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<GetAggregateTimelineResponse> GetAggregateTimelineAsync(
        GetAggregateTimelineRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.GetAggregateTimelineAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<GetProcessDestinationsResponse> GetProcessDestinationsAsync(
        GetProcessDestinationsRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.GetProcessDestinationsAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<GetCountryBreakdownResponse> GetCountryBreakdownAsync(
        GetCountryBreakdownRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.GetCountryBreakdownAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<GetProtocolBreakdownResponse> GetProtocolBreakdownAsync(
        GetProtocolBreakdownRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.GetProtocolBreakdownAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<GetProcessSummariesResponse> GetProcessSummariesAsync(
        GetProcessSummariesRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.GetProcessSummariesAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<GetFirewallActivityResponse> GetFirewallActivityAsync(
        GetFirewallActivityRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.GetFirewallActivityAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<ListLanDevicesResponse> ListLanDevicesAsync(
        ListLanDevicesRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.ListLanDevicesAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<TriggerScanResponse> TriggerScanAsync(
        TriggerScanRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.TriggerScanAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<SetLanDeviceLabelResponse> SetLanDeviceLabelAsync(
        SetLanDeviceLabelRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.SetLanDeviceLabelAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<GetStorageStatsResponse> GetStorageStatsAsync(
        GetStorageStatsRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.GetStorageStatsAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<GetSettingsResponse> GetSettingsAsync(
        GetSettingsRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.GetSettingsAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<SetRecordingSettingsResponse> SetRecordingSettingsAsync(
        SetRecordingSettingsRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.SetRecordingSettingsAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<SetHostnameResolutionSettingsResponse> SetHostnameResolutionSettingsAsync(
        SetHostnameResolutionSettingsRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.SetHostnameResolutionSettingsAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<SetAlertSettingsResponse> SetAlertSettingsAsync(
        SetAlertSettingsRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.SetAlertSettingsAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<SetScannerSettingsResponse> SetScannerSettingsAsync(
        SetScannerSettingsRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.SetScannerSettingsAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<SetTotalsSettingsResponse> SetTotalsSettingsAsync(
        SetTotalsSettingsRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.SetTotalsSettingsAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<AddAppIdentityRuleResponse> AddAppIdentityRuleAsync(
        AddAppIdentityRuleRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.AddAppIdentityRuleAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<RemoveAppIdentityRuleResponse> RemoveAppIdentityRuleAsync(
        RemoveAppIdentityRuleRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.RemoveAppIdentityRuleAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<ListAppIdentityRulesResponse> ListAppIdentityRulesAsync(
        ListAppIdentityRulesRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        var client = GetConnectedClient();
        return await client.ListAppIdentityRulesAsync(request, cancellationToken: cancellationToken);
    }

    public AsyncServerStreamingCall<DaemonEvent> Subscribe(CancellationToken cancellationToken) {
        var client = GetConnectedClient();
        return client.Subscribe(new SubscribeRequest(), cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync() {
        await _shutdownCts.CancelAsync();
        if (_connectTask is not null) {
            try {
                await _connectTask;
            } catch (OperationCanceledException) {
                // Today ConnectLoopAsync catches OCE internally and exits
                // cleanly, so this branch is defensive — if the loop is ever
                // changed to let OCE propagate, disposal still completes.
            }
        }
        _channel?.Dispose();
        _shutdownCts.Dispose();
    }

    private void SetState(ConnectionState newState) {
        if (_state == newState) return;
        _state = newState;
        StateChanged?.Invoke(StatusInfo);
    }

    /// <summary>
    /// Snapshots <see cref="_client"/> under the Connected-state check and
    /// returns it. Callers use the returned local for RPC dispatch, so even
    /// if the reconnect loop reassigns <see cref="_client"/> between this
    /// call and the RPC's await, the RPC runs against a consistent
    /// client/channel pair. If the snapshot happens to point at a just-
    /// disposed channel (narrow race), the RPC surfaces a normal
    /// <see cref="RpcException"/> / <see cref="ObjectDisposedException"/>
    /// which callers handle via the existing per-RPC catches in
    /// <c>TrafficTabViewModel</c> and <c>ProcessStateService</c>.
    /// </summary>
    private BeholderLocal.BeholderLocalClient GetConnectedClient() {
        var client = _client;
        if (_state != ConnectionState.Connected || client is null)
            throw new InvalidOperationException("Not connected to daemon");
        return client;
    }
}
