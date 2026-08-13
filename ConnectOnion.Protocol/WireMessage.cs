using System;
using System.Text.Json;

namespace ConnectOnion.Protocol;

/// <summary>
/// A decoded inbound WebSocket frame. Frames are JSON objects with a
/// <c>type</c> discriminator; the rest is read on demand via the helpers.
/// </summary>
public sealed class WireMessage
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowDuplicateProperties = false,
    };

    private readonly JsonElement _root;

    private WireMessage(JsonElement root) => _root = root;

    /// <summary>
    /// Parses a frame. The source <see cref="JsonDocument"/> is disposed before returning — it
    /// rents its backing buffers from <c>ArrayPool</c>, and a document that is never disposed
    /// never gives them back, so the pool re-allocates for the next frame. Frames carrying an
    /// LLM call's whole message history (or a base64 image) are big enough to land on the large
    /// object heap, which made that a visible, ratcheting cost over a long session. The clone is
    /// what survives: it owns its own bytes, independent of the pooled document.
    /// </summary>
    public static WireMessage Parse(string json)
    {
        using var doc = ParseDocument(json);
        return new WireMessage(doc.RootElement.Clone());
    }

    /// <summary>
    /// Parses an inbound frame for scoped, transient handling without cloning its backing bytes.
    /// The receive loop owns and disposes the returned document after all synchronous event
    /// consumers finish. Public callers use <see cref="Parse"/>, whose clone remains valid after
    /// the parse document is gone.
    /// </summary>
    internal static JsonDocument ParseDocument(string json)
        => JsonDocument.Parse(json, JsonOptions);

    /// <summary>Parses the receive loop's UTF-8 buffer directly. Keeping the frame as UTF-8 avoids
    /// allocating a second, twice-as-large UTF-16 string for large image events.</summary>
    internal static JsonDocument ParseDocument(ReadOnlyMemory<byte> utf8Json)
        => JsonDocument.Parse(utf8Json, JsonOptions);

    /// <summary>Wraps an element the caller has already parsed, so a reader that holds a live
    /// <see cref="JsonDocument"/> can reuse the typed accessors without paying for a second parse
    /// (and a second copy) of the same payload. The element must stay valid for the wrapper's
    /// lifetime — i.e. the caller's document must outlive it.</summary>
    public static WireMessage Wrap(JsonElement root) => new(root);

    public string Type => GetString("type") ?? "";

    public string? GetString(string name)
        => _root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public bool GetBool(string name)
        => _root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public bool TryGet(string name, out JsonElement value)
        => _root.TryGetProperty(name, out value);

    public JsonElement Root => _root;
}

/// <summary>
/// A streamed intermediate event surfaced to the UI while the agent works.
/// <see cref="RawJson"/> carries the fields the client needs to project and persist the event.
/// This is normally the full frame; transport-only <c>session_sync</c> frames are compacted before
/// buffering because their full form repeats the host's complete messages, trace, and permissions
/// on every iteration while the client consumes only scalar session state.
/// </summary>
/// <param name="EventId">Server-assigned event identifier, used as
/// <c>last_msg_id</c> on reconnect so the server can replay only missed events.</param>
public sealed record AgentStreamEvent(
    string Type,
    string Description,
    string? EventId,
    string RawJson = "{}",
    double? Timestamp = null);
