using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace ConnectOnion.WinUIClient.Services.Attachments;

/// <summary>
/// Turns a drag-and-drop payload into composer attachments. The drop counterpart to
/// <see cref="AttachmentPickerService"/>: both hand back <see cref="PendingAttachment"/>
/// drafts that still have to clear <see cref="AttachmentValidationService"/> before
/// <see cref="AttachmentEncoder"/> reads a single byte of them.
///
/// Unlike the picker there is no file-type filter to lean on — Explorer will happily
/// hand over a <c>.exe</c> or a folder — so this classifies each dropped file by
/// extension and lets validation reject anything unsupported with a readable message.
/// </summary>
public static class AttachmentDropService
{
    /// <summary>
    /// Whether the payload holds anything droppable at all. Cheap and synchronous, so it is
    /// safe to call from <c>DragOver</c>, which fires continuously while the pointer moves.
    /// </summary>
    public static bool ContainsFiles(DataPackageView data)
        => data.Contains(StandardDataFormats.StorageItems);

    /// <summary>
    /// Reads the dropped items and describes each one. Folders are skipped silently —
    /// there is no wire representation for a directory, and recursively expanding one
    /// would be a surprising amount of I/O to trigger from a drag gesture.
    /// </summary>
    public static async Task<IReadOnlyList<PendingAttachment>> ExtractAsync(
        DataPackageView data,
        CancellationToken ct = default)
    {
        if (!ContainsFiles(data)) return Array.Empty<PendingAttachment>();

        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await data.GetStorageItemsAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<PendingAttachment>();
        }

        var results = new List<PendingAttachment>(items.Count);
        foreach (var item in items)
        {
            if (item is not StorageFile file) continue;

            var kind = AttachmentValidationService.ClassifyKind(file.Name);
            var described = await AttachmentIntake.DescribeAsync(file, kind, ct).ConfigureAwait(false);
            if (described is not null) results.Add(described);
        }
        return results;
    }
}
