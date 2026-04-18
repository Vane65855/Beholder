using System;
using System.Threading;
using System.Threading.Tasks;
using Beholder.Protocol.Local;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Beholder.Ui.Services;

internal sealed class DaemonStreamSubscriber : IAsyncDisposable {
    private readonly IDaemonClient _daemonClient;
    private readonly ILogger<DaemonStreamSubscriber> _logger;
    private CancellationTokenSource? _cts;
    private Task? _consumeTask;

    public event Action<CounterBatch>? CounterBatchReceived;
    public event Action<FirewallRuleChange>? RuleChangeReceived;
    public event Action<AlertEvent>? AlertReceived;

    /// <summary>
    /// Async callback invoked after the daemon connection is established but
    /// before the live event stream starts. Used by <c>ProcessStateService</c>
    /// to seed per-process state from historical data so the UI doesn't show
    /// "0 B" on reconnect. Runs on the subscriber's background thread.
    /// </summary>
    public Func<CancellationToken, Task>? OnConnected { get; set; }

    public DaemonStreamSubscriber(
        IDaemonClient daemonClient,
        ILogger<DaemonStreamSubscriber> logger) {
        ArgumentNullException.ThrowIfNull(daemonClient);
        ArgumentNullException.ThrowIfNull(logger);
        _daemonClient = daemonClient;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _consumeTask = ConsumeLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        if (_cts is not null)
            await _cts.CancelAsync();

        if (_consumeTask is not null) {
            try {
                await _consumeTask;
            } catch (OperationCanceledException) {
                // Expected on shutdown
            }
        }
    }

    public async ValueTask DisposeAsync() {
        await StopAsync(CancellationToken.None);
        _cts?.Dispose();
    }

    private async Task ConsumeLoopAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            await WaitForConnected(cancellationToken);
            if (OnConnected is not null)
                await OnConnected(cancellationToken);
            await ConsumeStream(cancellationToken);
        }
    }

    private async Task WaitForConnected(CancellationToken cancellationToken) {
        if (_daemonClient.State == ConnectionState.Connected) return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        void OnStateChanged(DaemonStatusInfo status) {
            if (status.State == ConnectionState.Connected)
                tcs.TrySetResult();
        }

        _daemonClient.StateChanged += OnStateChanged;
        try {
            // Re-check after subscribing to avoid race
            if (_daemonClient.State == ConnectionState.Connected) return;
            await tcs.Task;
        } finally {
            _daemonClient.StateChanged -= OnStateChanged;
        }
    }

    private async Task ConsumeStream(CancellationToken cancellationToken) {
        try {
            using var call = _daemonClient.Subscribe(cancellationToken);
            await foreach (var daemonEvent in call.ResponseStream.ReadAllAsync(cancellationToken)) {
                DispatchEvent(daemonEvent);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested) {
            throw new OperationCanceledException(cancellationToken);
        } catch (RpcException ex) {
            _logger.LogInformation("Daemon stream ended: {Status}", ex.StatusCode);
        } catch (InvalidOperationException) {
            // DaemonClient.EnsureConnected threw — daemon disconnected between
            // our WaitForConnected check and the Subscribe call
            _logger.LogInformation("Daemon disconnected before stream could start");
        }
    }

    private void DispatchEvent(DaemonEvent daemonEvent) {
        switch (daemonEvent.PayloadCase) {
            case DaemonEvent.PayloadOneofCase.CounterBatch:
                CounterBatchReceived?.Invoke(daemonEvent.CounterBatch);
                break;
            case DaemonEvent.PayloadOneofCase.RuleChange:
                RuleChangeReceived?.Invoke(daemonEvent.RuleChange);
                break;
            case DaemonEvent.PayloadOneofCase.Alert:
                AlertReceived?.Invoke(daemonEvent.Alert);
                break;
        }
    }
}
