using System.Threading;
using System.Threading.Tasks;
using Beholder.Ui.Services;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IFileWriter"/>. Records the last write's path +
/// bytes for assertions; <see cref="Exception"/>, when non-null, is thrown
/// instead — exercises the VM's export-failure path.
/// </summary>
internal sealed class FakeFileWriter : IFileWriter {
    public string? LastPath { get; private set; }
    public byte[]? LastBytes { get; private set; }
    public int CallCount { get; private set; }
    public System.Exception? Exception { get; set; }

    public Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        LastPath = path;
        LastBytes = bytes;
        if (Exception is not null) throw Exception;
        return Task.CompletedTask;
    }
}
