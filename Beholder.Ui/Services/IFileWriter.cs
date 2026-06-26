using System.Threading;
using System.Threading.Tasks;

namespace Beholder.Ui.Services;

/// <summary>
/// Testable wrapper around writing a byte buffer to a filesystem path.
/// Introduced for Phase 11.3's "Export chain…" surface so the ViewModel can
/// persist the daemon's signed-export bytes to a user-chosen path without
/// touching <see cref="System.IO.File"/> directly (which would be untestable
/// and put filesystem I/O in the VM). Same service-seam shape as
/// <see cref="IShellOpener"/> / <see cref="IClipboardWriter"/> / <see cref="IFilePicker"/>.
/// </summary>
internal interface IFileWriter {
    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/>, overwriting
    /// any existing file. Throws on I/O failure (caller surfaces it to the user).
    /// </summary>
    Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken);
}
