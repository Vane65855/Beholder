namespace Beholder.Daemon;

/// <summary>
/// Lifecycle heartbeat. Prints one Information-level log line per second so the
/// operator can confirm the daemon process is alive. The real telemetry work
/// lives in <c>FlowEventPipeline</c>; this class exists only to make "is the
/// daemon still running?" visible without scraping metrics.
/// </summary>
public sealed class Worker(ILogger<Worker> logger) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            while (!stoppingToken.IsCancellationRequested) {
                logger.LogInformation("Worker running at: {Time}", DateTimeOffset.UtcNow);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Expected on shutdown.
        }
    }
}
