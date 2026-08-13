using System;
using System.IO;
using System.Threading;

namespace ConnectOnion.WinUIClient.Diagnostics;

/// <summary>
/// Writes a complete text file without exposing a partially-written document to readers.
/// Windows readers commonly omit <see cref="FileShare.Delete"/>, so replacing the destination
/// can fail briefly while a poller has it open. Retrying that narrow race keeps diagnostics from
/// losing their final update without weakening the atomic-write guarantee.
/// </summary>
internal static class AtomicTextFile
{
    internal static void WriteAllText(
        string path,
        string contents,
        int maxAttempts = 20,
        int retryDelayMilliseconds = 25,
        Action<int>? retryObserved = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(retryDelayMilliseconds);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, contents);

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, fullPath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (
                    (ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
                {
                    retryObserved?.Invoke(attempt);
                    Thread.Sleep(retryDelayMilliseconds);
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // The destination write has already failed with the actionable exception. A
                // best-effort temporary-file cleanup must not replace it with a secondary error.
            }
        }
    }
}
