using Beholder.Ui.Services;

namespace Beholder.Tests;

/// <summary>
/// Captures every <see cref="OpenFolder"/> invocation so tests can assert
/// the Settings tab's OpenDataFolderCommand passes the correct path to the
/// platform shell wrapper without launching a real Explorer window.
/// </summary>
internal sealed class FakeFolderOpener : IFolderOpener {
    public List<string> OpenedPaths { get; } = new();
    public Exception? Exception { get; set; }

    public void OpenFolder(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        OpenedPaths.Add(path);
        if (Exception is not null) throw Exception;
    }
}
