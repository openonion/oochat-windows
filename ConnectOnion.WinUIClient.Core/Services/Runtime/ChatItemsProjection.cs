using System.Text.Json;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>
/// Converts the host's <c>chat_items</c> array — the flattened, renderable form of a session
/// that rides along with a <c>server_newer</c> CONNECTED — into <see cref="ChatMessage"/> rows.
///
/// <para><b>This is a lossy, one-way import, and deliberately not the inverse of our own
/// projection.</b> The host builds chat_items from its session's <c>messages</c> + <c>trace</c>
/// (see <c>session_to_chat_items</c>), which is strictly less than what we store: item ids are
/// <i>positional</i> (<c>msg-0</c>, <c>thinking-4</c>) rather than stable, tool calls arrive
/// pre-flattened with no live status, and nothing carries attachments, cached image paths, or
/// the resolved outcome of an interactive card. So a chat_items row can never be matched
/// against one of our existing rows, and importing over a populated conversation would trade
/// richer local history for poorer remote history.</para>
///
/// <para>That is why the only caller adopts these rows when the local conversation is
/// <b>empty</b> — a fresh install, a reset database, a second device — and otherwise keeps what
/// it has. See <c>AgentSessionManager.OnSessionDiverged</c>.</para>
/// </summary>
public static class ChatItemsProjection
{
    /// <summary>
    /// Parses a raw <c>chat_items</c> JSON array. Ids are assigned sequentially from
    /// <paramref name="firstId"/> — the host's own ids are positional and would collide with
    /// the <c>(conversation_id, id)</c> key our repository allocates from <c>MAX(id)+1</c>.
    /// Returns an empty list for anything unparseable; a malformed import must not throw on a
    /// connection path.
    /// </summary>
    public static List<ChatMessage> Parse(string? chatItemsJson, string? agentName, long firstId = 1)
    {
        var messages = new List<ChatMessage>();
        if (string.IsNullOrWhiteSpace(chatItemsJson)) return messages;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(chatItemsJson); }
        catch (JsonException) { return messages; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return messages;

            var nextId = firstId;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var message = ToMessage(item, agentName, nextId);
                if (message is null) continue;
                messages.Add(message);
                nextId++;
            }
        }

        return messages;
    }

    /// <summary>
    /// The part of <c>chat_items</c> that follows the turn started by <paramref name="prompt"/> —
    /// i.e. everything the agent produced for that turn.
    ///
    /// <para>Recovers a turn the host finished while the app was closed. The client cannot resume
    /// such a turn (the host reports the session as idle, not running) but the reply is sitting in
    /// the <c>server_newer</c> payload that came back with CONNECTED, and it would otherwise be
    /// dropped on the floor.</para>
    ///
    /// <para><b>Alignment is by prompt, because ids cannot be used.</b> The host's item ids are
    /// positional within <i>its</i> session and have no relationship to our row ids. What both
    /// sides do agree on is the text of the user message that opened the turn, which the
    /// <c>executions</c> row preserves — so the anchor is the <b>last</b> user item whose content
    /// matches it (last, not first, because a user may well have sent the same words earlier in
    /// the conversation, and the turn we are recovering is the most recent one).</para>
    ///
    /// <para>Returns an empty list when the anchor is not found, when it is the final item
    /// (the agent produced nothing), or when the payload is unusable. Refusing to guess is the
    /// point: an unanchored append would interleave someone else's turn into the transcript.</para>
    /// </summary>
    public static List<ChatMessage> ParseTailAfterPrompt(
        string? chatItemsJson, string prompt, string? agentName, long firstId)
    {
        var tail = new List<ChatMessage>();
        if (string.IsNullOrWhiteSpace(chatItemsJson) || string.IsNullOrEmpty(prompt)) return tail;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(chatItemsJson); }
        catch (JsonException) { return tail; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return tail;

            var items = doc.RootElement.EnumerateArray().ToList();
            var anchor = -1;
            for (var i = items.Count - 1; i >= 0; i--)
            {
                var item = items[i];
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (Text(item, "type") != "user") continue;
                if (!string.Equals(Text(item, "content"), prompt, StringComparison.Ordinal)) continue;
                anchor = i;
                break;
            }

            if (anchor < 0) return tail;

            var nextId = firstId;
            for (var i = anchor + 1; i < items.Count; i++)
            {
                if (items[i].ValueKind != JsonValueKind.Object) continue;
                // A later user item means the host ran turns past the one we are recovering.
                // Stop rather than importing them: their own user messages are not in our
                // transcript, so appending their replies would leave answers with no questions.
                if (Text(items[i], "type") == "user") break;

                var message = ToMessage(items[i], agentName, nextId);
                if (message is null) continue;
                tail.Add(message);
                nextId++;
            }
        }

        return tail;
    }

    private static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static ChatMessage? ToMessage(JsonElement item, string? agentName, long id)
    {
        string? Str(string name)
            => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        return Str("type") switch
        {
            "user" => new ChatMessage
            {
                Id = id,
                Role = ChatRole.User,
                Content = Str("content") ?? "",
            },
            "agent" => new ChatMessage
            {
                Id = id,
                Role = ChatRole.Agent,
                AgentName = agentName,
                Content = Str("content") ?? "",
            },
            // The trace-derived kinds land as completed activity rows. They are history by
            // definition — the turn that produced them ended before this session was handed
            // back — so none of them can be Running, and none get a live control.
            "thinking" => Activity(id, "Thinking", Str("content")),
            "intent" => Activity(id, "Request understood", Str("ack")),
            "eval" => Activity(id, "Evaluation complete", Str("summary") ?? Str("expected")),
            "files_received" => Activity(id, "Files received", null),
            "tool_call" => Activity(id, $"Tool: {Str("name") ?? "unknown"}", Str("result")),
            // Anything else (a kind this build predates) is skipped rather than rendered as a
            // blank row — an imported history should not be padded with empty bubbles.
            _ => null,
        };
    }

    private static ChatMessage Activity(long id, string title, string? detail) => new()
    {
        Id = id,
        Role = ChatRole.Event,
        EventKind = "activity",
        EventTitle = title,
        EventDetail = detail,
        Status = EventStatus.Done,
    };
}
