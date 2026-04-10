namespace Beholder.Core;

/// <summary>
/// Platform-specific source of raw network telemetry. Implementations wrap an OS
/// mechanism (ETW on Windows, netlink/proc on Linux) and forward each observed event
/// to subscribers via <see cref="OnFlowEvent"/>.
/// </summary>
public interface IFlowSource {
    /// <summary>
    /// Begins capturing network events. Must be called before subscribers will receive
    /// any events. Throws if the underlying OS subsystem cannot be started (e.g.
    /// missing privileges).
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops capturing and releases all OS resources held by the provider. Safe to call
    /// even if <see cref="StartAsync"/> was never invoked.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Fired once per observed network event. Implementations raise this on whatever
    /// thread the OS callback delivers — subscribers are responsible for marshalling
    /// to a worker thread or dispatcher as needed and must return promptly.
    /// </summary>
    event Action<FlowEvent>? OnFlowEvent;
}
