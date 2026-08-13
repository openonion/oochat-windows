using System;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Models;
using Windows.Storage;

namespace ConnectOnion.WinUIClient.Services.Attachments;

/// <summary>
/// Shared <see cref="StorageFile"/> → <see cref="PendingAttachment"/> description step,
/// used by every route an attachment can enter the composer through
/// (<see cref="AttachmentPickerService"/> and <see cref="AttachmentDropService"/>).
/// Reads file *metadata* only — content is never touched until <see cref="AttachmentEncoder"/>
/// runs, which happens after validation passes, so an oversized or unsupported file is
/// rejected without any read I/O.
/// </summary>
internal static class AttachmentIntake
{
    /// <summary>
    /// Returns null when the file's metadata can't be read (locked, removed, or on a
    /// disconnected network share). Callers skip that file rather than failing the whole
    /// batch — one unreadable file must not discard the rest of a multi-file pick or drop.
    /// </summary>
    public static async Task<PendingAttachment?> DescribeAsync(
        StorageFile file,
        AttachmentKind kind,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        long sizeBytes;
        try
        {
            var props = await file.GetBasicPropertiesAsync().AsTask(ct).ConfigureAwait(false);
            sizeBytes = (long)props.Size;
        }
        catch
        {
            return null;
        }

        return new PendingAttachment
        {
            Kind = kind,
            FileName = file.Name,
            LocalPath = file.Path,
            MimeType = MimeTypeResolver.Resolve(file.ContentType, file.Name),
            SizeBytes = sizeBytes,
        };
    }
}
