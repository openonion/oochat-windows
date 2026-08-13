using System.Text.Json;
using System.Text.Json.Serialization;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Notifications;
using Microsoft.Data.Sqlite;

namespace ConnectOnion.WinUIClient.Data;

/// <summary>
/// Persisted shape of the session index plus the active id. SQLite stores the
/// list as rows and the active pointer in app metadata.
/// </summary>
public sealed class SessionsState
{
    [JsonPropertyName("sessions")]
    public List<SessionSummary> Sessions { get; set; } = new();

    [JsonPropertyName("activeSessionId")]
    public string? ActiveSessionId { get; set; }
}

/// <summary>
/// A bounded slice of one agent's conversations, newest first.
///
/// <para><b>Deliberately not a <see cref="SessionsState"/>.</b> <see cref="SessionRepository.SaveAsync"/>
/// reconciles the <i>whole</i> index — it upserts everything handed to it and deletes every row
/// that is not — so feeding it a partial list silently destroys the conversations that were not on
/// the page. Paged reads therefore return a type that has no path into that method at all, rather
/// than a comment asking callers to remember. See <see cref="SessionRepository.SaveAsync"/>.</para>
/// </summary>
/// <param name="HasMore">True when older conversations exist beyond this page. Answered by
/// fetching one extra row rather than a second <c>COUNT(*)</c>.</param>
public sealed record SessionPage(IReadOnlyList<SessionSummary> Sessions, bool HasMore)
{
    public static readonly SessionPage Empty = new(Array.Empty<SessionSummary>(), false);

    /// <summary>The keyset cursor to pass as <c>after</c> for the next page: the last row's
    /// <c>(updated_at, id)</c>. Null when there is nothing more to ask for.</summary>
    public (string UpdatedAt, string Id)? NextCursor
        => HasMore && Sessions.Count > 0
            ? (Sessions[^1].UpdatedAt, Sessions[^1].Id)
            : null;
}

/// <summary>
/// One agent's rolled-up unread state, across every conversation it owns.
/// </summary>
/// <param name="UnreadCount">Total unread across the agent's conversations.</param>
/// <param name="RequiresAttention">True when any of them is waiting on an approval.</param>
public readonly record struct AgentAttention(int UnreadCount, bool RequiresAttention);

/// <summary>
/// Local SQLite persistence for conversation index entries. Port of <c>sessionStorage.ts</c>.
///
/// <para><b>Prefer a targeted read over <see cref="LoadAsync"/>.</b> Most callers want one row by
/// id, a count, or the newest few — <see cref="GetSessionAsync"/>, <see cref="CountForAgentAsync"/>,
/// <see cref="LoadRecentAsync"/>, <see cref="LoadAgentSessionsAsync"/>. Reaching for the whole
/// index and then a <c>FirstOrDefault</c> reads every conversation the user has ever had in order
/// to find one.</para>
/// </summary>
public sealed class SessionRepository : IConversationAttentionStore
{
    /// <summary>Columns every summary read selects, in the order <see cref="ReadSummary"/> expects.</summary>
    private const string SummaryColumns =
        "id, agent_id, title, remote_session_id, last_processed_event_id, created_at, updated_at, mode, has_custom_title, unread_count, requires_attention";

    private const string ActiveSessionMetaKey = "active_session_id";

    // Pinned state is a JSON id list in app_meta rather than a column on `sessions`. It is a
    // small, whole-list property that is always read and written together, so a column plus a
    // migration would buy nothing over one metadata key.
    private const string PinnedSessionsMetaKey = "pinned_session_ids";

    /// <summary>Raised after <see cref="SaveAsync"/> commits, so the sidebar and any other
    /// open view refresh from a single signal instead of polling. Deliberately fires only for
    /// the whole-index save — <see cref="UpdateSessionAsync"/> edits a row the caller already
    /// has, so nothing else needs telling.</summary>
    public event System.Action? SessionsChanged;

    /// <summary>Updates only the active pointer; selecting a chat must not rewrite the index.</summary>
    public async Task SetActiveSessionAsync(
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sessionId))
            await AppDatabase.DeleteMetaAsync(connection, null, ActiveSessionMetaKey).ConfigureAwait(false);
        else
            await AppDatabase.SetMetaAsync(connection, null, ActiveSessionMetaKey, sessionId).ConfigureAwait(false);
        // No SessionsChanged here by design (selecting a chat is not an index change), but the
        // sidebar still repaints selection from it, so the revision must move.
        StorageRevision.Bump();
    }

    /// <summary>
    /// Loads sessions, dropping partial/corrupt rows (id, agentId and title all
    /// required) and clearing a stale active pointer.
    /// </summary>
    public async Task<SessionsState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        var state = new SessionsState
        {
            ActiveSessionId = await AppDatabase.GetMetaAsync(connection, ActiveSessionMetaKey).ConfigureAwait(false),
        };
        var pinnedIds = await LoadPinnedIdsAsync(connection).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            // Same stability rule as the agent list: sort_order is the user's arrangement,
            // then most-recent-first, then id purely so rows never swap places between reads.
            command.CommandText = $"""
                SELECT {SummaryColumns}
                FROM sessions
                ORDER BY sort_order, updated_at DESC, id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                state.Sessions.Add(ReadSummary(reader, pinnedIds));
            }
        }

        state.Sessions = state.Sessions
            .Where(s => !string.IsNullOrWhiteSpace(s.Id)
                        && !string.IsNullOrWhiteSpace(s.AgentId)
                        && !string.IsNullOrWhiteSpace(s.Title))
            .ToList();

        if (state.ActiveSessionId is not null &&
            state.Sessions.All(s => s.Id != state.ActiveSessionId))
        {
            state.ActiveSessionId = null;
        }

        return state;
    }

    private static SessionSummary ReadSummary(SqliteDataReader reader, HashSet<string>? pinnedIds)
    {
        var id = reader.GetString(0);
        return new SessionSummary
        {
            Id = id,
            AgentId = reader.GetString(1),
            Title = reader.GetString(2),
            RemoteSessionId = ReadNullableString(reader, 3),
            LastProcessedEventId = ReadNullableString(reader, 4),
            CreatedAt = reader.GetString(5),
            UpdatedAt = reader.GetString(6),
            // The setter rejects anything the host wouldn't honour, so a stale or unknown mode
            // reads back as Safe rather than being silently trusted.
            Mode = reader.GetString(7),
            HasCustomTitle = reader.GetInt64(8) != 0,
            UnreadCount = reader.GetInt32(9),
            RequiresAttention = reader.GetInt64(10) != 0,
            IsPinned = pinnedIds?.Contains(id) ?? false,
        };
    }

    /// <summary>Increments unread state after notification policy determined that the user is not
    /// already looking at this conversation. Completion clears stale approval attention because
    /// there is no longer an action waiting, while preserving the unread count.</summary>
    public async Task MarkUnreadAsync(
        string conversationId,
        bool requiresAttention,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sessions
            SET unread_count = unread_count + 1,
                requires_attention = $requires_attention
            WHERE id = $id;
            """;
        AppDatabase.Add(command, "$id", conversationId);
        AppDatabase.Add(command, "$requires_attention", requiresAttention ? 1 : 0);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0) return;
        StorageRevision.Bump();
        SessionsChanged?.Invoke();
    }

    /// <summary>
    /// Unread totals per agent, for the sidebar's collapsed-branch rollup badge.
    ///
    /// <para>An aggregate rather than a sum over loaded rows, because the sidebar fetches
    /// conversations only for <i>expanded</i> agents — the branches that need this badge most are
    /// exactly the ones whose rows were never read. Returns only agents that have something to
    /// report, so the common case of an all-read sidebar is an empty dictionary.</para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, AgentAttention>> GetAgentAttentionAsync(
        CancellationToken cancellationToken = default)
    {
        var totals = new Dictionary<string, AgentAttention>(StringComparer.Ordinal);
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT agent_id, SUM(unread_count), MAX(requires_attention)
            FROM sessions
            WHERE unread_count > 0 OR requires_attention <> 0
            GROUP BY agent_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var agentId = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(agentId)) continue;
            totals[agentId] = new AgentAttention(reader.GetInt32(1), reader.GetInt64(2) != 0);
        }

        return totals;
    }

    /// <summary>Marks a conversation read when its chat surface is opened.</summary>
    public async Task ClearAttentionAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sessions
            SET unread_count = 0, requires_attention = 0
            WHERE id = $id AND (unread_count <> 0 OR requires_attention <> 0);
            """;
        AppDatabase.Add(command, "$id", conversationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0) return;
        StorageRevision.Bump();
        SessionsChanged?.Invoke();
    }

    /// <summary>
    /// One conversation by id, or null if it is not there.
    ///
    /// <para>This is what the six "load the index, then <c>FirstOrDefault(s =&gt; s.Id == x)</c>"
    /// call sites actually wanted. The lookup is on <c>sessions</c>'s primary key, so it is a seek
    /// rather than a scan of every conversation the user has.</para>
    /// </summary>
    public async Task<SessionSummary?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        var pinnedIds = await LoadPinnedIdsAsync(connection).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SummaryColumns} FROM sessions WHERE id = $id;";
        AppDatabase.Add(command, "$id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadSummary(reader, pinnedIds)
            : null;
    }

    /// <summary>The active conversation pointer on its own — one <c>app_meta</c> row, where reading
    /// it off a <see cref="LoadAsync"/> result meant materializing the whole index first.
    /// <para>Unlike <see cref="LoadAsync"/> this does not verify the pointer still names a live
    /// conversation; pair it with <see cref="GetSessionAsync"/> when that matters.</para></summary>
    public async Task<string?> GetActiveSessionIdAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await AppDatabase.GetMetaAsync(connection, ActiveSessionMetaKey).ConfigureAwait(false);
    }

    /// <summary>
    /// Just the ids of an agent's conversations. The child rows (messages, attachments, executions,
    /// traces) have to be deleted before the <c>sessions</c> rows they hang off — the FKs declare no
    /// <c>ON DELETE</c> — so a caller tearing an agent down needs the id list up front, and nothing
    /// else about those conversations.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListSessionIdsForAgentAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return Array.Empty<string>();

        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM sessions WHERE agent_id = $agent_id;";
        AppDatabase.Add(command, "$agent_id", agentId);

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    /// How many conversations an agent has. Exists because <c>SessionSummary.NewConversation</c>
    /// numbers a new conversation from exactly this integer, and computing it by loading the whole
    /// index and counting in memory was the most wasteful read in the app.
    /// </summary>
    public async Task<int> CountForAgentAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return 0;

        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions WHERE agent_id = $agent_id;";
        AppDatabase.Add(command, "$agent_id", agentId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long count ? (int)count : 0;
    }

    /// <summary>
    /// One page of an agent's conversations, newest first.
    ///
    /// <para>Keyset pagination on <c>(updated_at, id)</c>, never <c>OFFSET</c>. A conversation's
    /// <c>updated_at</c> moves to the front the moment a message lands in it, and under OFFSET that
    /// shifts every later row up by one — so the next page would skip a conversation entirely.
    /// A keyset cursor can at worst repeat a row that moved, which for a navigation tree is a
    /// cosmetic duplicate rather than a conversation the user cannot see. <c>id</c> is in the
    /// cursor because <c>updated_at</c> ties: two conversations touched in the same millisecond
    /// would otherwise make the cursor ambiguous and drop one.</para>
    ///
    /// <para>Matches <c>ix_sessions_agent_updated(agent_id, updated_at DESC)</c>. Ordering is a
    /// plain text comparison, which is chronological because timestamps are round-trip ISO-8601 —
    /// the same reason the sidebar sorts these with <c>StringComparer.Ordinal</c>.</para>
    /// </summary>
    public async Task<SessionPage> LoadAgentSessionsAsync(
        string agentId,
        int limit,
        (string UpdatedAt, string Id)? after = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (string.IsNullOrWhiteSpace(agentId)) return SessionPage.Empty;

        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        var pinnedIds = await LoadPinnedIdsAsync(connection).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = after is null
            ? $"""
                SELECT {SummaryColumns}
                FROM sessions
                WHERE agent_id = $agent_id
                ORDER BY updated_at DESC, id DESC
                LIMIT $limit;
                """
            : $"""
                SELECT {SummaryColumns}
                FROM sessions
                WHERE agent_id = $agent_id
                  AND (updated_at < $after_updated_at
                       OR (updated_at = $after_updated_at AND id < $after_id))
                ORDER BY updated_at DESC, id DESC
                LIMIT $limit;
                """;
        AppDatabase.Add(command, "$agent_id", agentId);
        // One extra row answers "is there more" without a second query.
        AppDatabase.Add(command, "$limit", limit + 1);
        if (after is { } cursor)
        {
            AppDatabase.Add(command, "$after_updated_at", cursor.UpdatedAt);
            AppDatabase.Add(command, "$after_id", cursor.Id);
        }

        // Capped: the capacity is a hint, and a caller passing a very large limit to mean "all of
        // them" would otherwise try to pre-allocate that many entries and throw OutOfMemory before
        // reading a row. The list still grows to whatever the query actually returns.
        var sessions = new List<SessionSummary>(Math.Min(limit + 1, 512));
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                sessions.Add(ReadSummary(reader, pinnedIds));
        }

        var hasMore = sessions.Count > limit;
        if (hasMore) sessions.RemoveAt(sessions.Count - 1);
        return new SessionPage(sessions, hasMore);
    }

    /// <summary>
    /// Which conversation to open when the user picks an <i>agent</i> rather than a conversation.
    ///
    /// <para>The SQL twin of <c>SessionSelection.FindExisting</c>, and it implements the identical
    /// rule: the active conversation wins whenever it belongs to this agent (so re-selecting the
    /// agent you are already talking to is a no-op), otherwise the agent's most recently touched
    /// one, otherwise null so the caller can decide whether to create one. The pure function
    /// remains the readable statement of that rule and its tests are the specification;
    /// <c>SessionRepositoryTests.ResolveForAgentAsync_AgreesWithSessionSelection</c> holds the two
    /// to the same answers so this cannot drift away from it.</para>
    ///
    /// <para>Two indexed reads instead of materializing every conversation to run a
    /// <c>FirstOrDefault</c> over it — this runs on every chat page load.</para>
    /// </summary>
    public async Task<SessionSummary?> ResolveForAgentAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;

        var activeId = await GetActiveSessionIdAsync(cancellationToken).ConfigureAwait(false);
        if (activeId is not null)
        {
            var active = await GetSessionAsync(activeId, cancellationToken).ConfigureAwait(false);
            // The agent check matters: the active conversation usually belongs to a *different*
            // agent (that is why the user is switching), and must not be returned then.
            if (active is not null && string.Equals(active.AgentId, agentId, StringComparison.Ordinal))
                return active;
        }

        var recent = await LoadAgentSessionsAsync(agentId, limit: 1, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return recent.Sessions.Count > 0 ? recent.Sessions[0] : null;
    }

    /// <summary>The newest conversations across every agent. Backs the tray's recent list, which
    /// wants a handful and used to take them from a full index read.</summary>
    public async Task<IReadOnlyList<SessionSummary>> LoadRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        var pinnedIds = await LoadPinnedIdsAsync(connection).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SummaryColumns}
            FROM sessions
            ORDER BY updated_at DESC, id DESC
            LIMIT $limit;
            """;
        AppDatabase.Add(command, "$limit", limit);

        var sessions = new List<SessionSummary>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            sessions.Add(ReadSummary(reader, pinnedIds));
        return sessions;
    }

    /// <summary>
    /// Every pinned conversation, across agents, newest first. Deliberately unpaged: the pinned set
    /// is bounded by how many the user chose to pin, and a pinned shortcut that needed scrolling to
    /// find would defeat the point of pinning it.
    /// </summary>
    public async Task<IReadOnlyList<SessionSummary>> LoadPinnedAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        var pinnedIds = await LoadPinnedIdsAsync(connection).ConfigureAwait(false);
        if (pinnedIds.Count == 0) return Array.Empty<SessionSummary>();

        await using var command = connection.CreateCommand();
        // The ids come from our own app_meta blob, but they are still bound as parameters rather
        // than interpolated — the same rule SaveAsync's NOT IN list follows.
        command.CommandText = $"""
            SELECT {SummaryColumns}
            FROM sessions
            WHERE id IN (SELECT value FROM json_each($ids))
            ORDER BY updated_at DESC, id DESC;
            """;
        AppDatabase.Add(
            command,
            "$ids",
            JsonSerializer.Serialize(pinnedIds.ToList(), AppJsonContext.Default.ListString));

        var sessions = new List<SessionSummary>(pinnedIds.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            sessions.Add(ReadSummary(reader, pinnedIds));
        return sessions;
    }

    /// <summary>
    /// Conversations whose <i>title</i> contains <paramref name="query"/>, newest first.
    ///
    /// <para>A leading-wildcard <c>LIKE</c>, so this is a scan — but of one short text column with
    /// a bounded result, not of the transcript, and it replaces loading every conversation into
    /// memory to filter it there. Searching what was actually <i>said</i> is a different index
    /// entirely: <c>ConversationRepository.SearchMessageContentAsync</c>.</para>
    /// </summary>
    public async Task<IReadOnlyList<SessionSummary>> SearchByTitleAsync(
        string query,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SessionSummary>();

        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        var pinnedIds = await LoadPinnedIdsAsync(connection).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // ESCAPE so a title search for a literal % or _ does not turn into a wildcard.
        command.CommandText = $"""
            SELECT {SummaryColumns}
            FROM sessions
            WHERE title LIKE $pattern ESCAPE '\'
            ORDER BY updated_at DESC, id DESC
            LIMIT $limit;
            """;
        var escaped = query.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        AppDatabase.Add(command, "$pattern", $"%{escaped}%");
        AppDatabase.Add(command, "$limit", limit);

        var sessions = new List<SessionSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            sessions.Add(ReadSummary(reader, pinnedIds));
        return sessions;
    }

    /// <summary>Bounded shell search across conversation titles and agent names. Unlike the old
    /// overlay path this never materializes the complete session index merely to filter it on the
    /// UI thread.</summary>
    public async Task<IReadOnlyList<SessionSummary>> SearchByTitleOrAgentAsync(
        string query,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SessionSummary>();

        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        var pinnedIds = await LoadPinnedIdsAsync(connection).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.agent_id, s.title, s.remote_session_id, s.last_processed_event_id,
                   s.created_at, s.updated_at, s.mode, s.has_custom_title,
                   s.unread_count, s.requires_attention
            FROM sessions AS s
            LEFT JOIN agents AS a ON a.id = s.agent_id
            WHERE s.title LIKE $pattern ESCAPE '\'
               OR a.name LIKE $pattern ESCAPE '\'
            ORDER BY s.updated_at DESC, s.id DESC
            LIMIT $limit;
            """;
        var escaped = query.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        AppDatabase.Add(command, "$pattern", $"%{escaped}%");
        AppDatabase.Add(command, "$limit", limit);

        var sessions = new List<SessionSummary>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            sessions.Add(ReadSummary(reader, pinnedIds));
        return sessions;
    }

    /// <summary>Loads a bounded set of transcript-search hits in one statement, avoiding one
    /// <c>GetSessionAsync</c> round trip per FTS result.</summary>
    public async Task<IReadOnlyList<SessionSummary>> LoadSessionsByIdsAsync(
        IReadOnlyCollection<string> sessionIds,
        CancellationToken cancellationToken = default)
    {
        if (sessionIds.Count == 0) return Array.Empty<SessionSummary>();

        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        var pinnedIds = await LoadPinnedIdsAsync(connection).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SummaryColumns}
            FROM sessions
            WHERE id IN (SELECT value FROM json_each($ids))
            ORDER BY updated_at DESC, id DESC;
            """;
        AppDatabase.Add(
            command,
            "$ids",
            JsonSerializer.Serialize(sessionIds.Distinct(StringComparer.Ordinal).ToList(), AppJsonContext.Default.ListString));

        var sessions = new List<SessionSummary>(sessionIds.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            sessions.Add(ReadSummary(reader, pinnedIds));
        return sessions;
    }

    /// <summary>
    /// Removes one conversation's complete local graph, its pin, and its active pointer in one
    /// transaction.
    ///
    /// <para>Replaces "load the index, remove one entry, hand the rest to <see cref="SaveAsync"/>",
    /// which reconciled every row to delete one — and which becomes outright destructive the moment
    /// a caller's list is a page rather than the whole index.</para>
    ///
    /// <para>The FKs deliberately do not cascade, so the child-first order below is load-bearing.
    /// Keeping every statement on this transaction means a failure cannot leave a conversation
    /// with only some of its history removed.</para>
    /// </summary>
    public async Task DeleteSessionAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM message_attachments WHERE conversation_id = $id;
                DELETE FROM messages WHERE conversation_id = $id;
                DELETE FROM trace_events WHERE conversation_id = $id;
                DELETE FROM executions WHERE conversation_id = $id;
                DELETE FROM sessions WHERE id = $id;
                """;
            AppDatabase.Add(command, "$id", sessionId);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await PruneDeletedSessionReferencesAsync(connection, transaction, [sessionId])
            .ConfigureAwait(false);

        await transaction.CommitAsync().ConfigureAwait(false);
        StorageRevision.Bump();
        SessionsChanged?.Invoke();
    }

    /// <summary>
    /// Removes every conversation graph belonging to one agent, plus their pins and active
    /// pointer, in one transaction. The agent row itself is retained.
    /// </summary>
    public async Task DeleteSessionsForAgentAsync(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return;

        await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);

        var removedIds = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM sessions WHERE agent_id = $agent_id;";
            AppDatabase.Add(select, "$agent_id", agentId);
            await using var reader = await select.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) removedIds.Add(reader.GetString(0));
        }

        if (removedIds.Count == 0)
        {
            await transaction.CommitAsync().ConfigureAwait(false);
            return;
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM message_attachments
                WHERE conversation_id IN (SELECT id FROM sessions WHERE agent_id = $agent_id);
                DELETE FROM messages
                WHERE conversation_id IN (SELECT id FROM sessions WHERE agent_id = $agent_id);
                DELETE FROM trace_events
                WHERE conversation_id IN (SELECT id FROM sessions WHERE agent_id = $agent_id);
                DELETE FROM executions
                WHERE conversation_id IN (SELECT id FROM sessions WHERE agent_id = $agent_id);
                DELETE FROM sessions WHERE agent_id = $agent_id;
                """;
            AppDatabase.Add(delete, "$agent_id", agentId);
            await delete.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await PruneDeletedSessionReferencesAsync(connection, transaction, removedIds)
            .ConfigureAwait(false);

        await transaction.CommitAsync().ConfigureAwait(false);
        StorageRevision.Bump();
        SessionsChanged?.Invoke();
    }

    /// <summary>Drops the active pointer when it names a conversation that has just been deleted,
    /// so nothing is left pointing at a row that no longer exists. Callers choose the replacement;
    /// this only guarantees the stale value does not survive the delete.</summary>
    internal static async Task PruneDeletedSessionReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> removedSessionIds)
    {
        if (removedSessionIds.Count == 0) return;

        var removed = removedSessionIds as HashSet<string>
            ?? removedSessionIds.ToHashSet(StringComparer.Ordinal);
        var pinnedIds = await LoadPinnedIdsAsync(connection, transaction).ConfigureAwait(false);
        if (pinnedIds.RemoveWhere(removed.Contains) > 0)
            await WritePinnedIdsAsync(connection, transaction, pinnedIds).ConfigureAwait(false);

        var active = await AppDatabase.GetMetaAsync(connection, transaction, ActiveSessionMetaKey)
            .ConfigureAwait(false);
        if (active is not null && removed.Contains(active))
            await AppDatabase.DeleteMetaAsync(connection, transaction, ActiveSessionMetaKey).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the mutable fields of one existing session row. <see cref="SaveAsync"/> reconciles
    /// the entire index — it upserts every session and deletes the ones that vanished — which is
    /// what you want after adding or removing a conversation, but is far too much for renaming the
    /// active one or stamping its last-seen event id, both of which happen on every message sent.
    /// Does nothing if the row isn't there; the caller is editing a session it just loaded.
    /// </summary>
    public async Task UpdateSessionAsync(SessionSummary session)
    {
        if (string.IsNullOrWhiteSpace(session.Id) ||
            string.IsNullOrWhiteSpace(session.AgentId) ||
            string.IsNullOrWhiteSpace(session.Title))
        {
            return;
        }

        await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sessions
            SET title = $title,
                has_custom_title = $has_custom_title,
                remote_session_id = $remote_session_id,
                last_processed_event_id = $last_processed_event_id,
                updated_at = $updated_at,
                mode = $mode
            WHERE id = $id;
            """;
        AppDatabase.Add(command, "$title", session.Title);
        AppDatabase.Add(command, "$has_custom_title", session.HasCustomTitle ? 1 : 0);
        AppDatabase.Add(command, "$remote_session_id", session.RemoteSessionId);
        AppDatabase.Add(command, "$last_processed_event_id", session.LastProcessedEventId);
        AppDatabase.Add(command, "$updated_at", session.UpdatedAt);
        AppDatabase.Add(command, "$mode", session.Mode);
        AppDatabase.Add(command, "$id", session.Id);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);

        StorageRevision.Bump();
        SessionsChanged?.Invoke();
    }

    /// <summary>
    /// Inserts one new conversation and (optionally) makes it active, without touching any other
    /// row. <see cref="SaveAsync"/> reconciles the entire index — every session upserted plus a
    /// <c>NOT IN</c> delete — which is what "the user removed a conversation" needs and what
    /// "the user started a conversation" very much does not: starting a chat cost one write per
    /// conversation the user had ever had.
    ///
    /// <para><c>sort_order</c> continues the existing sequence so the row lands at the end of the
    /// index, which is where the caller's in-memory <c>Sessions.Add</c> put it. Returns without
    /// writing if the summary is missing the fields <see cref="LoadAsync"/> requires, matching
    /// <see cref="SaveAsync"/>'s treatment of the same case.</para>
    /// </summary>
    public async Task AppendSessionAsync(SessionSummary session, bool makeActive = true)
    {
        if (string.IsNullOrWhiteSpace(session.Id) ||
            string.IsNullOrWhiteSpace(session.AgentId) ||
            string.IsNullOrWhiteSpace(session.Title))
        {
            return;
        }

        await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            // COALESCE(MAX(sort_order) + 1, 0) rather than a counted index: it is one scalar read
            // instead of loading the index, and an empty table still yields 0.
            insert.CommandText = """
                INSERT INTO sessions (id, agent_id, title, has_custom_title, remote_session_id, last_processed_event_id, created_at, updated_at, sort_order, mode)
                VALUES ($id, $agent_id, $title, $has_custom_title, $remote_session_id, $last_processed_event_id, $created_at, $updated_at,
                        (SELECT COALESCE(MAX(sort_order) + 1, 0) FROM sessions), $mode)
                ON CONFLICT(id) DO UPDATE SET
                    agent_id = excluded.agent_id,
                    title = excluded.title,
                    has_custom_title = excluded.has_custom_title,
                    remote_session_id = excluded.remote_session_id,
                    last_processed_event_id = excluded.last_processed_event_id,
                    updated_at = excluded.updated_at,
                    mode = excluded.mode;
                """;
            AppDatabase.Add(insert, "$id", session.Id);
            AppDatabase.Add(insert, "$agent_id", session.AgentId);
            AppDatabase.Add(insert, "$title", session.Title);
            AppDatabase.Add(insert, "$has_custom_title", session.HasCustomTitle ? 1 : 0);
            AppDatabase.Add(insert, "$remote_session_id", session.RemoteSessionId);
            AppDatabase.Add(insert, "$last_processed_event_id", session.LastProcessedEventId);
            AppDatabase.Add(insert, "$created_at", session.CreatedAt);
            AppDatabase.Add(insert, "$updated_at", session.UpdatedAt);
            AppDatabase.Add(insert, "$mode", session.Mode);
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // A brand-new conversation is never pinned, but the summary is the caller's object and
        // nothing stops it arriving pinned — honour it rather than silently dropping the flag.
        if (session.IsPinned)
        {
            var pinnedIds = await LoadPinnedIdsAsync(connection).ConfigureAwait(false);
            if (pinnedIds.Add(session.Id))
                await WritePinnedIdsAsync(connection, transaction, pinnedIds).ConfigureAwait(false);
        }

        if (makeActive)
        {
            await AppDatabase.SetMetaAsync(connection, transaction, ActiveSessionMetaKey, session.Id)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
        StorageRevision.Bump();
        SessionsChanged?.Invoke();
    }

    /// <summary>
    /// Pins or unpins one conversation. Pinned state is a JSON id list in <c>app_meta</c>
    /// (see <see cref="PinnedSessionsMetaKey"/>), so this touches exactly one metadata row and
    /// never the <c>sessions</c> table — where <see cref="SaveAsync"/> would have rewritten every
    /// row to record a flag that is not even stored on them.
    /// </summary>
    public async Task SetPinnedAsync(string sessionId, bool isPinned)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
        var pinnedIds = await LoadPinnedIdsAsync(connection).ConfigureAwait(false);
        var changed = isPinned ? pinnedIds.Add(sessionId) : pinnedIds.Remove(sessionId);
        if (!changed) return;

        await WritePinnedIdsAsync(connection, null, pinnedIds).ConfigureAwait(false);
        StorageRevision.Bump();
        SessionsChanged?.Invoke();
    }

    /// <summary>
    /// Reconciles the <b>entire</b> index against <paramref name="state"/>: every session in it is
    /// upserted, and every row that is <i>not</i> in it is deleted.
    ///
    /// <para><b>Only ever pass a list that is the whole index.</b> Handing this a subset — a page,
    /// a filtered view, one agent's conversations — deletes everything else the user has, silently
    /// and unrecoverably. That is why paged reads return <see cref="SessionPage"/>, which cannot
    /// reach this method, and why the production delete paths use
    /// <see cref="DeleteSessionAsync"/>/<see cref="DeleteSessionsForAgentAsync"/> instead of
    /// removing an entry and saving the remainder.</para>
    ///
    /// <para>Nothing in the app calls this any more; it survives as a seeding helper for tests,
    /// which is the one situation where "these are all the sessions there are" is true by
    /// construction. Adding one conversation is <see cref="AppendSessionAsync"/>, editing one is
    /// <see cref="UpdateSessionAsync"/>, pinning is <see cref="SetPinnedAsync"/>.</para>
    ///
    /// <para>Children first, as ever: the FKs declare no <c>ON DELETE</c>, so a session removed
    /// here while its messages remain raises a constraint violation that fails the whole save.</para>
    /// </summary>
    public async Task SaveAsync(SessionsState state)
    {
        await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);

        // Collect valid ids so we can delete stale rows after the upsert loop.
        var keepIds = new List<string>();

        for (var index = 0; index < state.Sessions.Count; index++)
        {
            var session = state.Sessions[index];
            if (string.IsNullOrWhiteSpace(session.Id) ||
                string.IsNullOrWhiteSpace(session.AgentId) ||
                string.IsNullOrWhiteSpace(session.Title))
            {
                continue;
            }

            keepIds.Add(session.Id);

            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO sessions (id, agent_id, title, has_custom_title, remote_session_id, last_processed_event_id, created_at, updated_at, sort_order, mode)
                VALUES ($id, $agent_id, $title, $has_custom_title, $remote_session_id, $last_processed_event_id, $created_at, $updated_at, $sort_order, $mode)
                ON CONFLICT(id) DO UPDATE SET
                    agent_id = excluded.agent_id,
                    title = excluded.title,
                    has_custom_title = excluded.has_custom_title,
                    remote_session_id = excluded.remote_session_id,
                    last_processed_event_id = excluded.last_processed_event_id,
                    updated_at = excluded.updated_at,
                    sort_order = excluded.sort_order,
                    mode = excluded.mode;
                """;
            AppDatabase.Add(upsert, "$id", session.Id);
            AppDatabase.Add(upsert, "$agent_id", session.AgentId);
            AppDatabase.Add(upsert, "$title", session.Title);
            AppDatabase.Add(upsert, "$has_custom_title", session.HasCustomTitle ? 1 : 0);
            AppDatabase.Add(upsert, "$remote_session_id", session.RemoteSessionId);
            AppDatabase.Add(upsert, "$last_processed_event_id", session.LastProcessedEventId);
            AppDatabase.Add(upsert, "$created_at", session.CreatedAt);
            AppDatabase.Add(upsert, "$updated_at", session.UpdatedAt);
            AppDatabase.Add(upsert, "$sort_order", index);
            AppDatabase.Add(upsert, "$mode", session.Mode);
            await upsert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // Remove sessions that are no longer in the list.
        // By this point, the caller has already cleaned up child rows
        // (messages/executions/traces) for removed sessions, so FK won't trip.
        //
        // That ordering is a contract this method cannot enforce, and getting it wrong is the
        // failure mode described on ConversationRepository.DeleteMessagesAsync: the FKs have
        // no ON DELETE, so a session row deleted while its messages still exist raises a
        // constraint violation and fails the whole save. Delete a conversation via
        // ConversationRepository first, then save the index.
        if (keepIds.Count > 0)
        {
            await using var del = connection.CreateCommand();
            del.Transaction = transaction;
            // The id list is built as numbered *parameters*, never interpolated text — session
            // ids are GUIDs today, but a NOT IN list assembled by string concatenation is an
            // injection waiting for the day an id becomes user-derived.
            // StringBuilder, not `CommandText +=`. SqliteCommand.CommandText is a property, so the
            // old loop read the whole SQL back and reallocated it twice per session — O(N^2)
            // character copying to build a statement that is itself only O(N).
            var sql = new System.Text.StringBuilder("DELETE FROM sessions WHERE id NOT IN (");
            for (var i = 0; i < keepIds.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                sql.Append('$').Append('p').Append(i);
                AppDatabase.Add(del, $"$p{i}", keepIds[i]);
            }
            sql.Append(");");
            del.CommandText = sql.ToString();
            await del.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        else
        {
            // Empty keep-list means the caller really did remove every session. Special-cased
            // because "NOT IN ()" is not valid SQL — the loop above would emit it.
            await using var del = connection.CreateCommand();
            del.Transaction = transaction;
            del.CommandText = "DELETE FROM sessions;";
            await del.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(state.ActiveSessionId))
        {
            await AppDatabase.DeleteMetaAsync(connection, transaction, ActiveSessionMetaKey)
                .ConfigureAwait(false);
        }
        else
        {
            await AppDatabase.SetMetaAsync(connection, transaction, ActiveSessionMetaKey, state.ActiveSessionId!)
                .ConfigureAwait(false);
        }

        // Intersected with keepIds so the pinned list can never outlive the sessions it names
        // — nothing enforces referential integrity on a JSON blob in app_meta, so it is
        // pruned here instead.
        var pinnedIds = state.Sessions
            .Where(session => session.IsPinned && keepIds.Contains(session.Id))
            .Select(session => session.Id)
            .Distinct()
            .ToList();
        await WritePinnedIdsAsync(connection, transaction, pinnedIds).ConfigureAwait(false);

        await transaction.CommitAsync().ConfigureAwait(false);
        StorageRevision.Bump();
        SessionsChanged?.Invoke();
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>Persists the pinned-id list, deleting the metadata row entirely when nothing is
    /// pinned so an empty list never lingers as <c>"[]"</c>. Shared by the whole-index save and
    /// the single-conversation <see cref="SetPinnedAsync"/> so both agree on that representation.</summary>
    private static async Task WritePinnedIdsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IEnumerable<string> pinnedIds)
    {
        // Materialized as List<string> because that is the type the source-generated context has
        // a converter for — AppJsonContext.Default.ListString, not an interface.
        var ids = pinnedIds as List<string> ?? pinnedIds.ToList();
        if (ids.Count == 0)
        {
            await AppDatabase.DeleteMetaAsync(connection, transaction, PinnedSessionsMetaKey)
                .ConfigureAwait(false);
            return;
        }

        await AppDatabase.SetMetaAsync(
                connection,
                transaction,
                PinnedSessionsMetaKey,
                JsonSerializer.Serialize(ids, AppJsonContext.Default.ListString))
            .ConfigureAwait(false);
    }

    private static Task<HashSet<string>> LoadPinnedIdsAsync(SqliteConnection connection)
        => LoadPinnedIdsAsync(connection, null);

    private static async Task<HashSet<string>> LoadPinnedIdsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var json = await AppDatabase.GetMetaAsync(connection, transaction, PinnedSessionsMetaKey)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return new HashSet<string>();

        try
        {
            var ids = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListString) ?? new();
            return ids.ToHashSet();
        }
        catch
        {
            // Unreadable pin list → nothing pinned. Losing pins is a cosmetic regression the
            // user can redo in a click; throwing here would make the whole session index
            // unloadable, which is not.
            return new HashSet<string>();
        }
    }
}
