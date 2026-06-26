using System.Threading;
using System.Threading.Tasks;
using Beholder.Ui.Services;

namespace Beholder.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IFilePicker"/>. <see cref="PickedPath"/> is
/// the path returned on the next call; <c>null</c> simulates user-cancel.
/// <see cref="Exception"/>, when non-null, is thrown instead — exercises the
/// VM's error-path catch.
/// </summary>
internal sealed class FakeFilePicker : IFilePicker {
    public string? PickedPath { get; set; }
    public System.Exception? Exception { get; set; }
    public int CallCount { get; private set; }
    public string? LastTitle { get; private set; }

    /// <summary>Path returned by the next <see cref="PickSaveFileAsync"/>; null = cancel.</summary>
    public string? SavePickedPath { get; set; }
    public int SaveCallCount { get; private set; }
    public string? LastSaveTitle { get; private set; }
    public string? LastSuggestedFileName { get; private set; }

    public Task<string?> PickFileAsync(string title, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        LastTitle = title;
        if (Exception is not null) throw Exception;
        return Task.FromResult(PickedPath);
    }

    public Task<string?> PickSaveFileAsync(
        string title, string suggestedFileName, CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        SaveCallCount++;
        LastSaveTitle = title;
        LastSuggestedFileName = suggestedFileName;
        if (Exception is not null) throw Exception;
        return Task.FromResult(SavePickedPath);
    }
}
