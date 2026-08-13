using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services.Attachments;

/// <summary>
/// Client-side preflight validation. This is scope-limiting only — the extension
/// allow-list is a UI convenience (and matches the FileOpenPicker filter), not the
/// source of the agent's real capability. The server (<c>validate_files</c> in
/// <c>network/host/config.py</c>) remains the final authority; these checks exist
/// so a doomed send fails fast with a readable message instead of round-tripping
/// to the server first.
/// </summary>
public static class AttachmentValidationService
{
    public static readonly IReadOnlyCollection<string> ImageExtensions =
        new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    public static readonly IReadOnlyCollection<string> FileExtensions =
        new[] { ".pdf", ".txt", ".md", ".csv", ".json", ".doc", ".docx", ".ppt", ".pptx" };

    // Applied to `images` too: the server enforces NO size/count limit on images at
    // all (network/host/config.py:validate_files only checks `files`), and used as
    // the fallback for `files` when /info couldn't be fetched (capability unknown).
    private const int FallbackMaxFileSizeMb = 10;
    private const int FallbackMaxCount = 10;

    /// <summary>
    /// Gates on the agent's advertised capability. <paramref name="accepted"/> is
    /// null when /info was never successfully fetched — capability is unknown, so
    /// this does not block (a real rejection will still surface as a readable
    /// ERROR frame from the server if the agent truly doesn't support it).
    /// </summary>
    public static string? ValidateKindAllowed(AttachmentKind kind, AgentAcceptedInputs? accepted)
    {
        if (accepted is null) return null;

        if (kind == AttachmentKind.Image && accepted.Images == false)
            return "This agent does not accept image input.";

        // /info always includes a `files` object with limits for every real
        // ConnectOnion server today, so this branch is defense-in-depth for a
        // future server (or a relay-only profile) that omits it entirely.
        if (kind == AttachmentKind.File && accepted.Files is null)
            return "This agent does not accept file input.";

        return null;
    }

    /// <summary>
    /// Infers the attachment kind from a file's extension, for entry points that don't
    /// already know it (drag-and-drop, where the OS applies no filter). Anything that
    /// isn't a known image extension is classified as <see cref="AttachmentKind.File"/>
    /// — including extensions on neither list. Those are *not* filtered out here on
    /// purpose: letting them through to <see cref="ValidateExtension"/> turns an
    /// unsupported drop into a visible "Unsupported file type: .exe" chip instead of a
    /// file that silently vanishes on drop.
    /// </summary>
    public static AttachmentKind ClassifyKind(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        foreach (var candidate in ImageExtensions)
        {
            if (string.Equals(candidate, ext, StringComparison.OrdinalIgnoreCase)) return AttachmentKind.Image;
        }
        return AttachmentKind.File;
    }

    public static string? ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "File name is empty.";
        if (fileName.Length > 255) return "File name is too long.";
        if (fileName.Contains("..", StringComparison.Ordinal) ||
            fileName.Contains('/') || fileName.Contains('\\'))
        {
            return "Invalid file name.";
        }
        return null;
    }

    public static string? ValidateExtension(AttachmentKind kind, string fileName)
    {
        var ext = Path.GetExtension(fileName);
        var allowed = kind == AttachmentKind.Image ? ImageExtensions : FileExtensions;
        foreach (var candidate in allowed)
        {
            if (string.Equals(candidate, ext, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return $"Unsupported file type: {(string.IsNullOrEmpty(ext) ? "(no extension)" : ext)}";
    }

    /// <summary>
    /// Compares the *encoded* size (what the server actually measures — see
    /// <see cref="DataUrlCodec"/>) against the advertised limit, from the file's
    /// stat'd size alone — no content read required to reject an oversized file.
    /// </summary>
    public static string? ValidateSize(long rawSizeBytes, string mimeType, AgentFileInputs? limits)
    {
        var maxMb = limits is { MaxFileSizeMb: > 0 } ? limits.MaxFileSizeMb : FallbackMaxFileSizeMb;
        var maxBytes = (long)maxMb * 1024 * 1024;
        var estimated = DataUrlCodec.EstimateEncodedLength(rawSizeBytes, mimeType);
        return estimated > maxBytes
            ? $"File exceeds the agent's {maxMb} MB limit."
            : null;
    }

    public static string? ValidateCount(int currentCountOfSameKind, AgentFileInputs? limits)
    {
        var maxCount = limits is { MaxFilesPerRequest: > 0 } ? limits.MaxFilesPerRequest : FallbackMaxCount;
        return currentCountOfSameKind >= maxCount
            ? $"Too many attachments (max {maxCount})."
            : null;
    }

    /// <summary>Runs every check in order, returning the first failure (or null if the candidate is valid).</summary>
    public static string? Validate(PendingAttachment candidate, AgentAcceptedInputs? accepted, int existingCountOfSameKind)
    {
        return ValidateKindAllowed(candidate.Kind, accepted)
            ?? ValidateFileName(candidate.FileName)
            ?? ValidateExtension(candidate.Kind, candidate.FileName)
            ?? ValidateCount(existingCountOfSameKind, accepted?.Files)
            ?? ValidateSize(candidate.SizeBytes, candidate.MimeType, accepted?.Files);
    }
}
