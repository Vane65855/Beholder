using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Beholder.Ui.Services;

/// <summary>
/// Default <see cref="IClipboardWriter"/> — resolves the active
/// <see cref="IClipboard"/> via the supplied <see cref="TopLevel"/>-lookup
/// callback. The callback indirection exists because in Avalonia, the
/// clipboard is reached through whichever <see cref="TopLevel"/> (window)
/// the call is associated with — and the UI's composition root doesn't
/// hand a window reference to ViewModels.
/// </summary>
/// <remarks>
/// At construction time we accept a function that returns the current
/// <see cref="TopLevel"/> (typically lambda <c>() =&gt; TopLevel.GetTopLevel(mainWindow)</c>).
/// Calling the function on each write — rather than capturing the
/// <see cref="TopLevel"/> once — handles the edge case where the window is
/// torn down and re-created mid-session. Returns silently when no
/// TopLevel or clipboard is available; the user's click was a no-op but
/// the application stays alive.
/// </remarks>
internal sealed class AvaloniaClipboardWriter : IClipboardWriter {
    private readonly Func<TopLevel?> _topLevelAccessor;

    public AvaloniaClipboardWriter(Func<TopLevel?> topLevelAccessor) {
        ArgumentNullException.ThrowIfNull(topLevelAccessor);
        _topLevelAccessor = topLevelAccessor;
    }

    public Task WriteTextAsync(string? text, CancellationToken cancellationToken) {
        if (string.IsNullOrEmpty(text)) return Task.CompletedTask;
        var clipboard = _topLevelAccessor()?.Clipboard;
        if (clipboard is null) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        return clipboard.SetTextAsync(text);
    }
}
