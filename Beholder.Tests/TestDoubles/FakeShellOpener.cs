using Beholder.Ui.Services;

namespace Beholder.Tests;

/// <summary>
/// Captures every <see cref="Open"/> invocation so tests can assert the
/// Settings tab's open-folder / open-URL commands pass the correct target
/// to the platform shell wrapper without launching real Explorer / browser
/// windows. Replaces the older <c>FakeFolderOpener</c> alongside the
/// production-side <c>IFolderOpener</c> → <c>IShellOpener</c> rename.
/// </summary>
internal sealed class FakeShellOpener : IShellOpener {
    public List<string> OpenedTargets { get; } = new();
    public List<string> RevealedPaths { get; } = new();
    public Exception? Exception { get; set; }

    public void Open(string target) {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        OpenedTargets.Add(target);
        if (Exception is not null) throw Exception;
    }

    public void RevealInFolder(string filePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        RevealedPaths.Add(filePath);
        if (Exception is not null) throw Exception;
    }
}
