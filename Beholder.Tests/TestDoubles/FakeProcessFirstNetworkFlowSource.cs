using Beholder.Daemon.Pipeline;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IProcessFirstNetworkFlowSource"/>. Lets tests
/// raise <see cref="OnProcessFirstNetworkFlow"/> deterministically without
/// the engine + flow-source plumbing.
/// </summary>
internal sealed class FakeProcessFirstNetworkFlowSource : IProcessFirstNetworkFlowSource {
    public event Action<string>? OnProcessFirstNetworkFlow;

    public void Raise(string processPath) =>
        OnProcessFirstNetworkFlow?.Invoke(processPath);

    public bool HasSubscribers => OnProcessFirstNetworkFlow is not null;
}
