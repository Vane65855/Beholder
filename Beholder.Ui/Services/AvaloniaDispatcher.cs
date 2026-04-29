using System;
using Avalonia.Threading;

namespace Beholder.Ui.Services;

/// <summary>
/// Production <see cref="IDispatcher"/> adapter that delegates to Avalonia's
/// <c>Dispatcher.UIThread</c>. Registered as a singleton in the composition
/// root and threaded into every view-model that marshals event-handler
/// callbacks from background threads to the UI thread.
/// </summary>
/// <remarks>
/// This adapter is the only place in <c>Beholder.Ui</c> that names
/// <c>Avalonia.Threading.Dispatcher.UIThread</c>; everything else depends on
/// the <see cref="IDispatcher"/> abstraction. Verifiable via
/// <c>grep -rn "Dispatcher.UIThread" Beholder.Ui/ --include='*.cs'</c>.
/// </remarks>
internal sealed class AvaloniaDispatcher : IDispatcher {
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
