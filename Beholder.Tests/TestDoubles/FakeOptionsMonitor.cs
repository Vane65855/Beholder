using Microsoft.Extensions.Options;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Minimal <see cref="IOptionsMonitor{TOptions}"/> for tests. Returns the
/// current value and notifies registered listeners on <see cref="Set"/> so
/// live-reload code paths can be exercised. The production pipeline reads
/// <c>CurrentValue</c> on each event, so tests that don't need reload can
/// construct once and ignore <c>Set</c>.
/// </summary>
internal sealed class FakeOptionsMonitor<T> : IOptionsMonitor<T> where T : class {
    private readonly List<Action<T, string?>> _listeners = new();
    public T CurrentValue { get; private set; }

    public FakeOptionsMonitor(T value) {
        CurrentValue = value;
    }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) {
        _listeners.Add(listener);
        return new ListenerSubscription(() => _listeners.Remove(listener));
    }

    /// <summary>
    /// Replaces <see cref="CurrentValue"/> and notifies every listener
    /// registered via <see cref="OnChange"/>. Simulates the live-reload
    /// signal that the production <see cref="OptionsMonitor{T}"/> fires when
    /// <c>appsettings.json</c> changes.
    /// </summary>
    public void Set(T value) {
        CurrentValue = value;
        foreach (var listener in _listeners.ToArray())
            listener(value, null);
    }

    private sealed class ListenerSubscription(Action dispose) : IDisposable {
        public void Dispose() => dispose();
    }
}
