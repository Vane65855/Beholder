using System;

namespace Beholder.Ui.Services;

/// <summary>
/// Thin abstraction over Avalonia's UI-thread dispatcher so view-models can
/// be tested without a headless dispatcher fixture. Production wiring uses
/// <see cref="AvaloniaDispatcher"/>; tests use a synchronous fake.
/// </summary>
internal interface IDispatcher {
    /// <summary>Queue <paramref name="action"/> on the UI thread (fire-and-forget).</summary>
    void Post(Action action);
}
