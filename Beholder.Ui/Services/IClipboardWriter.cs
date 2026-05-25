using System.Threading;
using System.Threading.Tasks;

namespace Beholder.Ui.Services;

/// <summary>
/// Testable wrapper around the platform clipboard, used by the Settings
/// tab's copy-icon buttons (data folder path, project URL). The default
/// implementation routes through Avalonia's
/// <c>TopLevel.GetTopLevel(window).Clipboard</c>; tests inject a fake that
/// captures the most-recent value without touching the OS clipboard.
/// </summary>
internal interface IClipboardWriter {
    /// <summary>
    /// Writes <paramref name="text"/> to the system clipboard as plain
    /// text. No-op when <paramref name="text"/> is null or empty.
    /// </summary>
    Task WriteTextAsync(string? text, CancellationToken cancellationToken);
}
