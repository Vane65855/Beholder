using Microsoft.Extensions.Options;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Minimal <see cref="IOptionsMonitor{TOptions}"/> for tests. Returns a fixed
/// value and never fires change notifications — the production pipeline
/// reads <c>CurrentValue</c> on each event, so tests only need a stable
/// snapshot to exercise the filter path.
/// </summary>
internal sealed class FakeOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class {
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
