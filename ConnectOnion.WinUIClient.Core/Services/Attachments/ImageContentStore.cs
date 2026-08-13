using System.Security.Cryptography;
using ConnectOnion.WinUIClient.Data;

namespace ConnectOnion.WinUIClient.Services.Attachments;

/// <summary>
/// Stores conversation images under a SHA-256 content address. Both user-selected images and
/// agent-produced images pass through this one writer, so SQLite can reference an app-owned file
/// instead of a movable source path or an in-memory data URL.
/// </summary>
public static class ImageContentStore
{
    public const long MaxImageBytes = 50 * 1024 * 1024;

    public static async Task<string?> StoreBytesAsync(
        byte[] bytes,
        string mimeType,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var source = new MemoryStream(bytes, writable: false);
        return await StoreStreamAsync(source, mimeType, MaxImageBytes, ct).ConfigureAwait(false);
    }

    public static async Task<string?> StoreFileAsync(
        string sourcePath,
        string mimeType,
        CancellationToken ct = default)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await StoreStreamAsync(source, mimeType, MaxImageBytes, ct).ConfigureAwait(false);
    }

    /// <summary>Copies and hashes a stream in one pass, then atomically publishes the final path.</summary>
    public static async Task<string?> StoreStreamAsync(
        Stream source,
        string mimeType,
        long maxBytes = MaxImageBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!IsImageMime(mimeType) || maxBytes <= 0) return null;

        Directory.CreateDirectory(AppStorage.ImageCacheDir);
        var tempPath = Path.Combine(
            AppStorage.ImageCacheDir, $".{Guid.NewGuid():N}.image.tmp");

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > maxBytes) return null;

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                if (total == 0) return null;
                await output.FlushAsync(ct).ConfigureAwait(false);
            }

            var fileName = Convert.ToHexString(hash.GetHashAndReset()) + ExtensionFor(mimeType);
            var finalPath = Path.Combine(AppStorage.ImageCacheDir, fileName);
            if (File.Exists(finalPath)) return finalPath;

            try
            {
                File.Move(tempPath, finalPath);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // A concurrent writer published the same content first. Its file is equivalent.
            }

            return finalPath;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // A failed cleanup is an orphan, never a reason to fail the chat turn. The cache
                // maintenance sweep will reclaim it once it passes the minimum-age guard.
            }
        }
    }

    private static bool IsImageMime(string mimeType)
        => mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static string ExtensionFor(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".bin",
    };
}
