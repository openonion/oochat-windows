using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.Protocol;

namespace ConnectOnion.WinUIClient.Services.Attachments;

/// <summary>
/// Resolves an inbound image reference (from an <c>agent_image</c> event) into a
/// locally cached file, so the chat message list and SQLite never hold a
/// multi-megabyte base64 string directly (see task requirement: no giant base64 in
/// the ViewModel or DB — only metadata + cache path).
///
/// Only <c>data:image/*</c> and <c>http(s)://</c> sources are accepted — current
/// source only ever produces the data-URL form (<c>network/io/base.py:send_image</c>),
/// but the http(s) path is kept for forward-compatibility since nothing in this
/// codebase should assume agent images are *always* one specific form. Any other
/// scheme (<c>file:</c>, <c>javascript:</c>, <c>data:text/html</c>, ...) is rejected.
/// </summary>
public static class AttachmentImageCacheService
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(15);
    private const int MaxCompletedDataUrls = 64;
    private static readonly object InflightGate = new();
    private static readonly Dictionary<string, Task<(string LocalPath, string MimeType)?>> Inflight =
        new(StringComparer.Ordinal);
    private static readonly object CompletedGate = new();
    private static readonly Dictionary<string, (string LocalPath, string MimeType)> Completed =
        new(StringComparer.Ordinal);
    private static readonly Queue<string> CompletedOrder = new();

    public static async Task<(string LocalPath, string MimeType)?> ResolveAndCacheAsync(
        string source, HttpClient http, CancellationToken ct = default)
    {
        var kind = DataUrlCodec.ClassifyImageSource(source);
        var key = Fingerprint(source);
        if (kind == ImageSourceKind.DataUrl)
        {
            lock (CompletedGate)
            {
                if (Completed.TryGetValue(key, out var cached) && File.Exists(cached.LocalPath))
                    return cached;
            }
        }

        Task<(string LocalPath, string MimeType)?> work;
        lock (InflightGate)
        {
            if (!Inflight.TryGetValue(key, out work!))
            {
                work = ResolveCoreAsync(source, http, kind, ct);
                Inflight[key] = work;
            }
        }
        try
        {
            var resolved = await work.ConfigureAwait(false);
            if (kind == ImageSourceKind.DataUrl && resolved is { } value)
                RememberCompleted(key, value);
            return resolved;
        }
        finally
        {
            lock (InflightGate)
            {
                if (Inflight.TryGetValue(key, out var current) && ReferenceEquals(current, work))
                    Inflight.Remove(key);
            }
        }
    }

    private static async Task<(string LocalPath, string MimeType)?> ResolveCoreAsync(
        string source,
        HttpClient http,
        ImageSourceKind kind,
        CancellationToken ct)
    {
        // Ensure the potentially expensive base64 decode/download does not run while the caller
        // is still holding InflightGate to publish this shared task.
        await Task.Yield();
        switch (kind)
        {
            case ImageSourceKind.DataUrl:
                byte[] bytes;
                string mime;
                if (!DataUrlCodec.TryDecode(source, out mime, out bytes)) return null;
                if (!IsImageMime(mime)) return null;
                var localPath = await ImageContentStore
                    .StoreBytesAsync(bytes, mime, ct)
                    .ConfigureAwait(false);
                return localPath is null ? null : (localPath, mime);

            case ImageSourceKind.HttpUrl:
                return await TryDownloadAsync(source, http, ct).ConfigureAwait(false);

            default:
                return null;
        }
    }

    private static void RememberCompleted(string key, (string LocalPath, string MimeType) value)
    {
        lock (CompletedGate)
        {
            if (Completed.ContainsKey(key)) return;
            Completed[key] = value;
            CompletedOrder.Enqueue(key);
            while (CompletedOrder.Count > MaxCompletedDataUrls)
            {
                var expired = CompletedOrder.Dequeue();
                Completed.Remove(expired);
            }
        }
    }

    private static string Fingerprint(string source)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var encoder = Encoding.UTF8.GetEncoder();
        var bytes = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var offset = 0;
            while (offset < source.Length)
            {
                var take = Math.Min(2048, source.Length - offset);
                var flush = offset + take == source.Length;
                encoder.Convert(
                    source.AsSpan(offset, take),
                    bytes,
                    flush,
                    out var charsUsed,
                    out var bytesUsed,
                    out _);
                hash.AppendData(bytes, 0, bytesUsed);
                offset += charsUsed;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static async Task<(string LocalPath, string MimeType)?> TryDownloadAsync(
        string url, HttpClient http, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DownloadTimeout);
            using var response = await http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var mime = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!IsImageMime(mime)) return null;
            if (response.Content.Headers.ContentLength is > ImageContentStore.MaxImageBytes)
                return null;

            await using var stream = await response.Content
                .ReadAsStreamAsync(cts.Token)
                .ConfigureAwait(false);
            var path = await ImageContentStore
                .StoreStreamAsync(stream, mime, ImageContentStore.MaxImageBytes, cts.Token)
                .ConfigureAwait(false);
            return path is null ? null : (path, mime);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Timeout, DNS failure, non-2xx, etc. — treat as "couldn't load", not a crash.
            return null;
        }
    }

    private static bool IsImageMime(string mime)
        => mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

}
