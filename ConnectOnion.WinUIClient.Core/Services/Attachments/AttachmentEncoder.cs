using ConnectOnion.Protocol;

namespace ConnectOnion.WinUIClient.Services.Attachments;

/// <summary>
/// Encodes a validated attachment file into the wire's <c>data:</c> URL format.
/// Reads in aligned pooled chunks so a large send never holds the complete raw file and its
/// Base64 representation at the same time. Encoding is deliberately send-time only.
/// </summary>
public static class AttachmentEncoder
{
    public static Task<string> EncodeToDataUrlAsync(string localPath, string mimeType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var fileLength = new FileInfo(localPath).Length;
        var encodedLength = DataUrlCodec.EstimateEncodedLength(fileLength, mimeType);
        if (encodedLength > int.MaxValue)
            throw new IOException("Attachment is too large to encode as a data URL.");

        // string.Create writes into the final string's storage. The previous StringBuilder path
        // held its large backing char[] and then copied the entire payload again in ToString(), so
        // a 50 MiB file briefly needed two ~133 MiB UTF-16 buffers before JSON serialization even
        // began. Encoding runs on the pool because string.Create's callback is synchronous; none
        // of its file I/O is allowed to stall the WinUI thread.
        return Task.Run(
            () => EncodeIntoFinalString(localPath, mimeType, checked((int)encodedLength), ct),
            ct);
    }

    private static string EncodeIntoFinalString(
        string localPath,
        string mimeType,
        int encodedLength,
        CancellationToken ct)
    {
        const int chunkSize = 48 * 1024; // divisible by three: chunk boundaries preserve Base64.
        var prefix = $"data:{mimeType};base64,";
        return string.Create(
            encodedLength,
            (localPath, prefix, ct),
            static (destination, state) =>
            {
                state.prefix.AsSpan().CopyTo(destination);
                var destinationOffset = state.prefix.Length;
                var buffer = new byte[chunkSize];
                using var stream = new FileStream(
                    state.localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: chunkSize,
                    FileOptions.SequentialScan);

                while (true)
                {
                    state.ct.ThrowIfCancellationRequested();
                    var count = 0;
                    while (count < buffer.Length)
                    {
                        var read = stream.Read(buffer, count, buffer.Length - count);
                        if (read == 0) break;
                        count += read;
                    }

                    if (count == 0) break;
                    if (!Convert.TryToBase64Chars(
                            buffer.AsSpan(0, count), destination[destinationOffset..], out var written))
                    {
                        throw new InvalidOperationException("The data-URL destination was sized incorrectly.");
                    }
                    destinationOffset += written;
                    if (count < buffer.Length) break;
                }

                if (destinationOffset != destination.Length)
                    throw new IOException("Attachment length changed while it was being encoded.");
            });
    }
}
