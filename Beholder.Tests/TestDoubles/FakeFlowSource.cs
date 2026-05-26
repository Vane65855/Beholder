using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

internal sealed class FakeFlowSource : IFlowSource {
    public event Action<FlowEvent>? OnFlowEvent;

    /// <summary>
    /// Test helper: synchronously fires <see cref="OnFlowEvent"/> with the
    /// given event. Lets tests exercise the subscriber's per-event behaviour
    /// (e.g., the FlowEventPipeline filter-self-traffic gate) without
    /// wiring up real ETW.
    /// </summary>
    public void Fire(FlowEvent flowEvent) => OnFlowEvent?.Invoke(flowEvent);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
