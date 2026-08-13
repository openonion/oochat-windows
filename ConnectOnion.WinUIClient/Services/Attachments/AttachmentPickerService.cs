using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Models;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace ConnectOnion.WinUIClient.Services.Attachments;

/// <summary>
/// Wraps <see cref="FileOpenPicker"/> for attachment selection. HWND init follows
/// the same pattern already used for the folder picker in
/// <c>MainWindow.FileMenu.cs</c>'s <c>PickAndOpenFolderAsync</c>, but sourced from
/// <see cref="App.MainWindow"/> since the composer button lives inside a
/// <see cref="Microsoft.UI.Xaml.Controls.UserControl"/> (no HWND of its own).
/// </summary>
public static class AttachmentPickerService
{
    /// <summary>
    /// Opens a multi-select file picker filtered to the given attachment kind.
    /// Returns an empty list if the user cancels the picker — that is a normal
    /// outcome, not an error, and callers must not treat it as a failure.
    /// Only reads file metadata (name, size, content type) here; no file content
    /// is read until <c>AttachmentEncoder</c> runs after validation passes.
    /// </summary>
    public static async Task<IReadOnlyList<PendingAttachment>> PickAsync(AttachmentKind kind, CancellationToken ct = default)
    {
        if (App.MainWindow is null) return Array.Empty<PendingAttachment>();

        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        foreach (var ext in kind == AttachmentKind.Image
                     ? AttachmentValidationService.ImageExtensions
                     : AttachmentValidationService.FileExtensions)
        {
            picker.FileTypeFilter.Add(ext);
        }

        IReadOnlyList<StorageFile>? files;
        try
        {
            files = await picker.PickMultipleFilesAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<PendingAttachment>();
        }

        if (files is null || files.Count == 0) return Array.Empty<PendingAttachment>();

        var results = new List<PendingAttachment>(files.Count);
        foreach (var file in files)
        {
            // A file whose metadata can't be read is skipped rather than failing the
            // whole batch pick.
            var described = await AttachmentIntake.DescribeAsync(file, kind, ct).ConfigureAwait(false);
            if (described is not null) results.Add(described);
        }
        return results;
    }
}
