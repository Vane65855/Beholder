using Beholder.Core;

namespace Beholder.Tests.TestDoubles;

internal sealed class FakeFlowSource : IFlowSource {
#pragma warning disable CS0067 // Event is required by IFlowSource but not exercised in these tests
    public event Action<FlowEvent>? OnFlowEvent;
#pragma warning restore CS0067
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
