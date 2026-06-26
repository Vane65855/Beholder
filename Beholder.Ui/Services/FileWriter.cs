using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Beholder.Ui.Services;

/// <summary>Default <see cref="IFileWriter"/> — writes via <see cref="File.WriteAllBytesAsync"/>.</summary>
internal sealed class FileWriter : IFileWriter {
    public Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken) =>
        File.WriteAllBytesAsync(path, bytes, cancellationToken);
}
