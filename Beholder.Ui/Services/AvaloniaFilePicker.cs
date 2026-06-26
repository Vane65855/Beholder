using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Beholder.Ui.Services;

/// <summary>
/// Default <see cref="IFilePicker"/> — resolves the active
/// <see cref="IStorageProvider"/> via the supplied
/// <see cref="TopLevel"/>-lookup callback. Same indirection rationale as
/// <see cref="AvaloniaClipboardWriter"/>: in Avalonia, file-picker dialogs
/// are reached through whichever <see cref="TopLevel"/> the call is
/// associated with, and the UI's composition root doesn't hand a window
/// reference to ViewModels.
/// </summary>
internal sealed class AvaloniaFilePicker : IFilePicker {
    private readonly Func<TopLevel?> _topLevelAccessor;

    public AvaloniaFilePicker(Func<TopLevel?> topLevelAccessor) {
        ArgumentNullException.ThrowIfNull(topLevelAccessor);
        _topLevelAccessor = topLevelAccessor;
    }

    public async Task<string?> PickFileAsync(string title, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        var storage = _topLevelAccessor()?.StorageProvider;
        if (storage is null) return null;

        // Default to *.exe on Windows; relaxed to "all files" on other platforms.
        // The "All files" entry is always present so users can pick service
        // hosts / MSIX-wrapped binaries that don't end in .exe.
        var fileTypes = new List<FilePickerFileType>();
        if (OperatingSystem.IsWindows()) {
            fileTypes.Add(new FilePickerFileType("Executables") {
                Patterns = ["*.exe"],
            });
        }
        fileTypes.Add(new FilePickerFileType("All files") {
            Patterns = ["*"],
        });

        var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = fileTypes,
        }).ConfigureAwait(true);

        var picked = result.FirstOrDefault();
        if (picked is null) return null;

        // TryGetLocalPath returns the filesystem path when the picked file is
        // a regular local file; null for virtual / remote files (e.g., a
        // network drive that wasn't mapped, or an Android content URI on a
        // future port). For Phase 13.6's "pick a binary" use case, only
        // local filesystem paths make sense — null returns yield a no-op.
        return picked.TryGetLocalPath();
    }

    public async Task<string?> PickSaveFileAsync(
        string title, string suggestedFileName, CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        var storage = _topLevelAccessor()?.StorageProvider;
        if (storage is null) return null;

        var result = await storage.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = [
                new FilePickerFileType("JSON") { Patterns = ["*.json"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        }).ConfigureAwait(true);

        return result?.TryGetLocalPath();
    }
}
