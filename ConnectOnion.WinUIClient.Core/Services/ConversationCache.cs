using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Keeps the last few *idle* conversations in memory so re-opening one skips the database read
/// and the bubble re-projection. A conversation with an active run is never served from here —
/// the chat page must load the authoritative history the run manager persisted before replaying
/// the run's events on top of it.
///
/// Eviction is real LRU (touched on every read <i>and</i> write), and entries are dropped when
/// the conversation they belong to is deleted — a cache that never forgets a deleted session
/// would pin its whole message list, attachments and all, for the lifetime of the process.
/// </summary>
public static class ConversationCache
{
    private const int MaxEntries = 4;
    private const long MaxEstimatedBytes = 16L * 1024 * 1024;

    /// <param name="Messages">The currently loaded bounded message list.</param>
    public sealed record Entry(
        AgentConfig Agent,
        SessionSummary Session,
        List<ChatMessage> Messages,
        long CreatedAt,
        bool HasMoreBefore = false);

    private static readonly Dictionary<string, LinkedListNode<string>> Nodes = new();
    private static readonly Dictionary<string, Entry> Entries = new();
    private static readonly Dictionary<string, long> Sizes = new();
    private static long _estimatedBytes;

    // Most-recently-used at the front, so the tail is always the eviction victim.
    private static readonly LinkedList<string> Recency = new();

    private static readonly object Gate = new();

    internal static int CountForTests
    {
        get { lock (Gate) return Entries.Count; }
    }

    /// <summary>Returns the cached conversation and marks it most-recently-used. Misses when the
    /// entry was cached for a different agent, which guards against an id collision serving a
    /// conversation into the wrong agent's page.</summary>
    public static Entry? Get(string sessionId, string agentId)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(sessionId, out var entry)) return null;
            if (entry.Agent.Id != agentId) return null;

            Touch(sessionId);
            return entry;
        }
    }

    /// <summary>Caches a conversation, evicting down to <see cref="MaxEntries"/>. Overwrites an
    /// existing entry rather than merging — the caller always holds the full, freshly loaded
    /// message list, so a partial update is never what is wanted here.</summary>
    public static void Store(string sessionId, Entry entry)
    {
        // Measured outside the lock. It walks every message and every attachment, and the entry
        // is not reachable by anyone else yet, so there is nothing to guard — holding Gate for
        // the walk only blocks concurrent Get/Invalidate callers for the duration.
        var size = EstimateBytes(entry);

        lock (Gate)
        {
            if (Sizes.TryGetValue(sessionId, out var previousSize)) _estimatedBytes -= previousSize;
            Entries[sessionId] = entry;
            Sizes[sessionId] = size;
            _estimatedBytes += size;
            Touch(sessionId);

            // Bound both entry count and retained payload. Four tiny chats are useful; one huge
            // transcript is not worth pinning merely because it counts as a single entry.
            while ((Entries.Count > MaxEntries || _estimatedBytes > MaxEstimatedBytes)
                   && Recency.Last is { } victim)
            {
                RemoveLocked(victim.Value);
            }
        }
    }

    /// <summary>Drops a conversation from the cache — call when its messages are deleted, so a
    /// removed session leaves no in-memory copy behind.</summary>
    public static void Invalidate(string sessionId)
    {
        lock (Gate)
        {
            RemoveLocked(sessionId);
        }
    }

    private static void RemoveLocked(string sessionId)
    {
        if (!Entries.Remove(sessionId)) return;
        if (Sizes.Remove(sessionId, out var size)) _estimatedBytes -= size;
        if (Nodes.Remove(sessionId, out var node)) Recency.Remove(node);
    }

    private static long EstimateBytes(Entry entry)
    {
        long bytes = 512;
        bytes += StringBytes(entry.Agent.Name) + StringBytes(entry.Agent.Address)
            + StringBytes(entry.Agent.DirectUrl) + StringBytes(entry.Session.Title);

        foreach (var message in entry.Messages)
        {
            bytes += 512;
            bytes += StringBytes(message.Content) + StringBytes(message.AgentName)
                + StringBytes(message.EventKind) + StringBytes(message.EventKey)
                + StringBytes(message.EventEyebrow) + StringBytes(message.EventTitle)
                + StringBytes(message.EventDetail) + StringBytes(message.EventMeta)
                + StringBytes(message.EventArgs) + StringBytes(message.EventResult);
            foreach (var attachment in message.Attachments)
            {
                bytes += 256 + StringBytes(attachment.FileName) + StringBytes(attachment.MimeType)
                    + StringBytes(attachment.LocalCachePath) + StringBytes(attachment.RemoteUri);
            }
        }

        return bytes;
    }

    private static long StringBytes(string? value) => value is null ? 0 : value.Length * sizeof(char);

    /// <summary>Moves a session to the front of the recency list. <c>Nodes</c> exists purely to
    /// make this O(1) — without the node handle, promoting an entry would mean an O(n) scan of
    /// the list on every cache read.</summary>
    private static void Touch(string sessionId)
    {
        if (Nodes.TryGetValue(sessionId, out var node))
        {
            // The same node instance is re-inserted, which is what keeps the handle in Nodes
            // valid. Re-adding by value instead would leave Nodes pointing at a detached node
            // and quietly break every later Remove.
            Recency.Remove(node);
            Recency.AddFirst(node);
            return;
        }

        Nodes[sessionId] = Recency.AddFirst(sessionId);
    }
}
