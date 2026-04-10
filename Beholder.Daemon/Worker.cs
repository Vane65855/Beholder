using Beholder.Core;

namespace Beholder.Daemon;

public sealed class Worker(ILogger<Worker> logger, IFlowSource? flowSource = null) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (flowSource is null) {
            logger.LogWarning("No IFlowSource registered — running in heartbeat-only mode");
            await HeartbeatLoopAsync(stoppingToken);
            return;
        }

        flowSource.OnFlowEvent += LogFlowEvent;
        try {
            await flowSource.StartAsync(stoppingToken);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Expected on shutdown.
        } finally {
            flowSource.OnFlowEvent -= LogFlowEvent;
            await flowSource.StopAsync(CancellationToken.None);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            logger.LogInformation("Worker running at: {Time}", DateTimeOffset.UtcNow);
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private void LogFlowEvent(FlowEvent flowEvent) {
        logger.LogInformation(
            "Flow {Process} ({Pid}) {Remote}:{Port} in={BytesIn} out={BytesOut}",
            flowEvent.ProcessName,
            flowEvent.ProcessId,
            flowEvent.RemoteAddress,
            flowEvent.RemotePort,
            flowEvent.BytesIn,
            flowEvent.BytesOut);
    }
}
