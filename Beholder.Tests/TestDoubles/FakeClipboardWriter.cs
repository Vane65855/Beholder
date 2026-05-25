using Beholder.Ui.Services;

namespace Beholder.Tests;

/// <summary>
/// Captures clipboard-write invocations so tests can assert the Settings
/// tab's copy-icon buttons pass the correct text without touching the OS
/// clipboard.
/// </summary>
internal sealed class FakeClipboardWriter : IClipboardWriter {
    public List<string?> Writes { get; } = new();
    public Exception? Exception { get; set; }

    public Task WriteTextAsync(string? text, CancellationToken cancellationToken) {
        Writes.Add(text);
        if (Exception is not null) throw Exception;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
