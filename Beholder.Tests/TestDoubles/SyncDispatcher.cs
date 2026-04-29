using System;
using Beholder.Ui.Services;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IDispatcher"/> that runs the action immediately
/// on the calling thread. xunit has no Avalonia headless dispatcher fixture,
/// so production-code paths that call <c>IDispatcher.Post</c> run synchronously
/// under test — assertions follow without timing windows or polling.
/// </summary>
internal sealed class SyncDispatcher : IDispatcher {
    public void Post(Action action) => action();
}
