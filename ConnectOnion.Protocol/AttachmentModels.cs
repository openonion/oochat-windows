using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ConnectOnion.Protocol;

/// <summary>
/// One entry of the INPUT <c>files</c> array. Wire shape is exactly
/// <c>{"name": "...", "data": "data:&lt;mime&gt;;base64,..."}</c> — confirmed against
/// <c>connectonion/core/agent.py:236-238</c> (<c>f["name"]</c>, <c>f["data"]</c>) and the
/// TypeScript SDK's <c>remote-agent.ts:134</c> (<c>files.map(f =&gt; ({ name: f.name, data: f.dataUrl }))</c>).
/// There is no separate mime/size field on the wire — mime lives in the data URL prefix,
/// size is never sent (the server measures <c>len(f["data"])</c> itself).
/// </summary>
public sealed record OutgoingFileAttachment(string Name, string DataUrl);

/// <summary>
/// One entry of a <c>files_received</c> event's <c>files</c> array — an echo of a file the
/// *user* just uploaded, saved server-side to <c>.co/uploads/&lt;ts&gt;_&lt;name&gt;</c>
/// (<c>agent.py:267-293</c>, <c>317-323</c>). <see cref="Path"/> is the server's local
/// filesystem path, not a fetchable URL — this event is a notification, not a download
/// mechanism (confirmed: no generic file-output/download event exists in source).
/// </summary>
public sealed record ReceivedFileRef(string Name, string Path);

/// <summary>
/// Typed parsers for the two real attachment-related wire events found in source:
/// <c>agent_image</c> (<c>network/io/base.py:113-122</c>, <c>IO.send_image</c>) and
/// <c>files_received</c> (<c>agent.py:317-323</c>, <c>host/session/ui.py:30-35</c>).
/// Mirrors the <see cref="WireMessage"/> convention (hand-parsed accessors, no
/// attribute-based deserialization) used elsewhere in this project (see
/// <c>InteractiveModels.cs</c>'s <c>ParseAskUser</c>).
/// </summary>
public static class AttachmentWireEvents
{
    /// <summary>
    /// Extracts the image data URL from an <c>agent_image</c> frame:
    /// <c>{"type": "agent_image", "image": "data:image/png;base64,..."}</c>.
    /// Only the data-URL form exists in current source (no http(s) URL, no
    /// relative <c>/img/...</c> form) — callers should still tolerate other
    /// URI forms defensively (see <see cref="ImageSourceKind"/>) since this is
    /// the only server-verified shape, not a hard wire guarantee.
    /// </summary>
    public static bool TryGetAgentImageDataUrl(WireMessage msg, out string dataUrl)
    {
        dataUrl = "";
        if (msg.Type != "agent_image") return false;
        var image = msg.GetString("image");
        if (string.IsNullOrEmpty(image)) return false;
        dataUrl = image;
        return true;
    }

    /// <summary>
    /// Extracts the file list from a <c>files_received</c> frame:
    /// <c>{"type": "files_received", "files": [{"name": "...", "path": "..."}]}</c>.
    /// Malformed entries (missing name, wrong element kind) are skipped rather
    /// than throwing — a bad entry must not drop the whole event or crash the
    /// receive loop.
    /// </summary>
    public static bool TryGetFilesReceived(WireMessage msg, out IReadOnlyList<ReceivedFileRef> files)
    {
        files = Array.Empty<ReceivedFileRef>();
        if (msg.Type != "files_received") return false;
        if (!msg.TryGet("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Array) return false;

        var list = new List<ReceivedFileRef>();
        foreach (var entry in filesEl.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
            var name = nameEl.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var path = entry.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String
                ? pathEl.GetString() ?? ""
                : "";
            list.Add(new ReceivedFileRef(name!, path));
        }
        files = list;
        return true;
    }
}

/// <summary>How an inbound image reference should be resolved into bytes.</summary>
public enum ImageSourceKind
{
    /// <summary>Unrecognized or unsupported URI scheme.</summary>
    Unsupported,
    DataUrl,
    HttpUrl,
}

/// <summary>
/// Base64 data URL codec, plus the encoded-length estimate the server actually
/// enforces against <c>max_file_size_mb</c>.
///
/// <c>network/host/config.py:validate_files</c> (line 141: <c>size = len(f["data"])</c>)
/// compares the *encoded string length* (data URL prefix + base64 payload) against
/// <c>max_size_bytes</c> — not the decoded file's raw byte count. Base64 has ~33%
/// overhead, so a file just under the configured limit in raw bytes can still be
/// rejected server-side once encoded. Client-side preflight must replicate this
/// exact comparison, not a raw-byte comparison, or "looks fine locally" will still
/// get rejected by the server.
/// </summary>
public static class DataUrlCodec
{
    public static string Encode(string mimeType, byte[] bytes)
        => $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";

    /// <summary>
    /// Decodes a data URL. Returns false (never throws) for missing prefix,
    /// missing/invalid ";base64" marker, or invalid base64 payload — malformed
    /// input from a remote peer must be a recoverable "couldn't load" state,
    /// not an exception that can take down a receive loop.
    /// </summary>
    public static bool TryDecode(string dataUrl, out string mimeType, out byte[] bytes)
    {
        mimeType = "";
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:", StringComparison.Ordinal))
            return false;

        var comma = dataUrl.IndexOf(',');
        if (comma < 0) return false;

        var header = dataUrl[5..comma];
        const string marker = ";base64";
        if (!header.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) return false;

        mimeType = header[..^marker.Length];
        if (string.IsNullOrWhiteSpace(mimeType)) mimeType = "application/octet-stream";

        try
        {
            bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Estimates the wire-encoded length of a file of <paramref name="rawByteCount"/>
    /// bytes with the given MIME type, i.e. what the server's
    /// <c>len(f["data"])</c> check will see — base64 (4 chars per 3 bytes,
    /// rounded up) plus the literal <c>data:&lt;mime&gt;;base64,</c> prefix.
    /// Lets preflight validation reject an oversized file from its stat'd size
    /// alone, without reading or encoding the content first.
    /// </summary>
    public static long EstimateEncodedLength(long rawByteCount, string mimeType)
    {
        if (rawByteCount <= 0) return "data:;base64,".Length + mimeType.Length;
        var base64Length = ((rawByteCount + 2) / 3) * 4;
        return base64Length + "data:;base64,".Length + mimeType.Length;
    }

    public static ImageSourceKind ClassifyImageSource(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return ImageSourceKind.Unsupported;
        if (uri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return ImageSourceKind.DataUrl;
        if (uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return ImageSourceKind.HttpUrl;
        return ImageSourceKind.Unsupported;
    }
}
