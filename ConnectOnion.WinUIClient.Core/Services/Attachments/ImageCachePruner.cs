using ConnectOnion.WinUIClient.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConnectOnion.WinUIClient.Services.Attachments;

/// <summary>
/// Garbage-collects the conversation-image cache (<see cref="AppStorage.ImageCacheDir"/>).
///
/// <para>The cache is written through <see cref="ImageContentStore"/> and never cleaned up
/// otherwise: deleting a conversation removes its <c>message_attachments</c> rows but leaves the
/// image files, so the directory only ever grows.</para>
///
/// <para><b>Only unreferenced files are deleted.</b> An <c>agent_image</c>'s base64 payload is
/// deliberately never persisted — the database holds the cache path and nothing else — so a
/// cached file is the *only* copy of that image. Deleting one that a conversation still points at
/// would blank the image in history permanently, which is why this is an orphan sweep and not a
/// size- or age-based LRU trim. Disk use is therefore bounded by what the conversations actually
/// reference, not by a cap.</para>
/// </summary>
public static class ImageCachePruner
{
    private static readonly Action<ILogger, Exception?> LogReferenceSetUnavailable =
        LoggerMessage.Define(LogLevel.Debug, new EventId(1, "ImageCacheSweepSkipped"),
            "Image cache sweep skipped: reference set unavailable.");

    private static readonly Action<ILogger, Exception?> LogEnumerationFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(2, "ImageCacheSweepEnumerationFailed"),
            "Image cache sweep could not enumerate the cache directory.");

    private static readonly Action<ILogger, string, Exception?> LogFileSkipped =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(3, "ImageCacheSweepFileSkipped"),
            "Image cache sweep skipped {File}.");

    private static readonly Action<ILogger, int, Exception?> LogSweepResult =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(4, "ImageCacheSweepCompleted"),
            "Image cache sweep deleted {Count} orphaned image(s).");

    /// <summary>
    /// How long a file is protected from the sweep regardless of whether anything references it.
    ///
    /// <para>This closes a real race: <see cref="ImageContentStore"/> writes the file during a
    /// turn, but the row that references it is not written until the turn is persisted at its
    /// end. A sweep landing in that window would see a legitimately in-use file with no referent
    /// and delete it. An hour is far longer than any turn, and the cost of being wrong in this
    /// direction (an orphan survives until the next sweep) is nothing.</para>
    /// </summary>
    private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Deletes cached images no conversation references any more. Returns the number of files
    /// deleted. Never throws — a maintenance sweep must not be able to take down startup.
    /// </summary>
    /// <param name="conversations">Source of the referenced-file set.</param>
    /// <param name="now">Injectable clock; the age guard is what a test needs to control.</param>
    public static async Task<int> PruneOrphansAsync(
        ConversationRepository conversations,
        ILogger? logger = null,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        logger ??= NullLogger.Instance;
        var cutoff = (now ?? DateTimeOffset.UtcNow) - MinimumAge;

        var referenced = await conversations.GetReferencedCacheFileNamesAsync(ct).ConfigureAwait(false);
        // Null means the query failed, which is not the same as "nothing is referenced".
        // Deleting on an unknown reference set would be the one mistake this sweep cannot undo.
        if (referenced is null)
        {
            LogReferenceSetUnavailable(logger, null);
            return 0;
        }

        string[] files;
        try
        {
            if (!Directory.Exists(AppStorage.ImageCacheDir)) return 0;
            files = Directory.GetFiles(AppStorage.ImageCacheDir);
        }
        catch (Exception ex)
        {
            LogEnumerationFailed(logger, ex);
            return 0;
        }

        var deleted = 0;
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (referenced.Contains(Path.GetFileName(file))) continue;
                if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                // A file locked by a live decode, or removed by another sweep between the
                // enumeration and here. Skip it; the next sweep picks it up.
                LogFileSkipped(logger, file, ex);
            }
        }

        if (deleted > 0) LogSweepResult(logger, deleted, null);
        return deleted;
    }
}
