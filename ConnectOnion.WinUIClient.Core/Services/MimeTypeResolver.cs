namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Resolves a MIME type for an outgoing attachment. Priority order:
/// 1. <c>StorageFile.ContentType</c> (when meaningful — Windows sometimes reports
///    the generic <c>application/octet-stream</c> for types it doesn't recognize)
/// 2. Extension mapping (covers everything <c>AttachmentValidationService</c> allows)
/// 3. <c>application/octet-stream</c> fallback
/// </summary>
public static class MimeTypeResolver
{
    private static readonly Dictionary<string, string> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    public static string Resolve(string? storageFileContentType, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(storageFileContentType) &&
            !storageFileContentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return storageFileContentType;
        }

        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && ExtensionMap.TryGetValue(ext, out var mime)
            ? mime
            : "application/octet-stream";
    }
}
