namespace Beholder.Daemon.Pipeline;

/// <summary>
/// Fires once per process path the first time the engine sees it on the
/// network during the daemon's current session. Phase 7's
/// <c>NewProcessDetector</c> subscribes here rather than to the raw
/// <c>IFlowSource</c> stream because (a) the engine consumer thread is safe
/// for detector work while the ETW callback thread is not, and (b) the
/// engine has the per-process bookkeeping that turns "every flow event"
/// into "first event per path".
/// </summary>
/// <remarks>
/// Exposed as a separate interface so consumers don't depend on the
/// concrete <see cref="FlowEventPipeline"/> type. Mirrors
/// <see cref="ISnapshotBatchSource"/>.
/// </remarks>
internal interface IProcessFirstNetworkFlowSource {
    /// <summary>
    /// Fires once per session-scoped process path. Handlers run on the
    /// engine consumer thread and must not block. Daemon-restart
    /// deduplication is the subscriber's job (the engine's bookkeeping is
    /// session-scoped, not durable).
    /// </summary>
    event Action<string>? OnProcessFirstNetworkFlow;
}
