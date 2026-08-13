using System.Text.Json;
using ConnectOnion.WinUIClient.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConnectOnion.WinUIClient.Data;

/// <summary>A bounded slice of a conversation, ordered oldest to newest.</summary>
public sealed record ConversationPage(IReadOnlyList<ChatMessage> Messages, bool HasMoreBefore);

/// <summary>
/// Row-level message persistence. Each chat bubble is a row in the
/// <c>messages</c> table, keyed by <c>(conversation_id, id)</c>.
/// Replaces the previous JSON blob approach so messages can be queried
/// and written incrementally.
/// </summary>
public sealed partial class ConversationRepository
{
    public const int DefaultPageSize = 160;
    private static readonly Action<ILogger, string, string, Exception?> LogDatabaseFailure =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(1, "ConversationDatabaseFailure"),
            "Database operation {Operation} failed for conversation {ConversationId}");
    private readonly ILogger<ConversationRepository> _logger;

    public ConversationRepository(ILogger<ConversationRepository>? logger = null)
        => _logger = logger ?? NullLogger<ConversationRepository>.Instance;

    /// <remarks>
    /// Approval bubbles are filtered out of every read. A settled approval is not shown anywhere
    /// in the transcript — the tool-activity step and the agent's reply already report what came
    /// of it — so the reads exclude them rather than relying on nothing having written one. That
    /// also covers rows left in databases from before the persist path stopped storing them; the
    /// old rows stay on disk (harmless, and no migration to get wrong) but never surface.
    /// </remarks>
    public async Task<IReadOnlyList<ChatMessage>> LoadMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>();
        try
        {
            await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, role, content, agent_name,
                       event_kind, event_key, event_eyebrow, event_title,
                       event_detail, event_meta, event_args, event_result,
                       event_status, is_onboarding, created_at
                FROM messages
                WHERE conversation_id = $conversation_id AND event_kind IS NOT 'approval'
                ORDER BY id;
                """;
            AppDatabase.Add(command, "$conversation_id", conversationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                messages.Add(RowToMessage(reader));
            }

            await LoadAttachmentsIntoAsync(connection, conversationId, messages, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        // Cancellation is not a failure — rethrow it so a page that navigated away during the
        // load doesn't get logged as a database error, and doesn't silently receive a partial
        // conversation as if it were the whole thing.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Missing / corrupt table → start fresh. Note `messages` may already hold rows
            // read before the failure; they are returned rather than discarded, since a
            // partial history still beats an empty one for a user staring at the page.
            LogFailure(nameof(LoadMessagesAsync), conversationId, ex);
        }
        return messages;
    }

    /// <summary>
    /// Loads only the newest bounded page of a conversation. Fetching one extra row answers
    /// whether an older page exists without a separate <c>COUNT(*)</c> scan.
    /// </summary>
    public Task<ConversationPage> LoadRecentMessagesAsync(
        string conversationId,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
        => LoadMessagePageAsync(conversationId, beforeMessageId: null, pageSize, cancellationToken);

    /// <summary>Loads the page immediately before <paramref name="beforeMessageId"/>.</summary>
    public Task<ConversationPage> LoadMessagesBeforeAsync(
        string conversationId,
        long beforeMessageId,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
        => LoadMessagePageAsync(conversationId, beforeMessageId, pageSize, cancellationToken);

    private async Task<ConversationPage> LoadMessagePageAsync(
        string conversationId,
        long? beforeMessageId,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var messages = new List<ChatMessage>(pageSize + 1);

        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = beforeMessageId is null
                ? """
                    SELECT id, role, content, agent_name,
                           event_kind, event_key, event_eyebrow, event_title,
                           event_detail, event_meta, event_args, event_result,
                           event_status, is_onboarding, created_at
                    FROM messages
                    WHERE conversation_id = $conversation_id AND event_kind IS NOT 'approval'
                    ORDER BY id DESC
                    LIMIT $limit;
                    """
                : """
                    SELECT id, role, content, agent_name,
                           event_kind, event_key, event_eyebrow, event_title,
                           event_detail, event_meta, event_args, event_result,
                           event_status, is_onboarding, created_at
                    FROM messages
                    WHERE conversation_id = $conversation_id
                      AND event_kind IS NOT 'approval'
                      AND id < $before_id
                    ORDER BY id DESC
                    LIMIT $limit;
                    """;
            AppDatabase.Add(command, "$conversation_id", conversationId);
            AppDatabase.Add(command, "$limit", pageSize + 1);
            if (beforeMessageId is { } before) AppDatabase.Add(command, "$before_id", before);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                messages.Add(RowToMessage(reader));
        }

        var hasMore = messages.Count > pageSize;
        if (hasMore) messages.RemoveAt(messages.Count - 1);
        messages.Reverse();

        if (messages.Count > 0)
        {
            await LoadAttachmentsIntoAsync(
                    connection,
                    conversationId,
                    messages,
                    minimumMessageId: messages[0].Id,
                    maximumMessageId: messages[^1].Id,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        return new ConversationPage(messages, hasMore);
    }

    /// <summary>
    /// Attaches each row's attachments to the already-materialized messages, in one pass.
    ///
    /// Separate query rather than a JOIN on the message read: a JOIN would repeat every
    /// message column once per attachment and then need de-duplicating in code. Two queries
    /// and a dictionary is both cheaper and simpler. Pass <paramref name="onlyMessageId"/> to
    /// scope it to a single bubble (the <see cref="LoadLastAgentMessageAsync"/> path).
    /// </summary>
    private static async Task LoadAttachmentsIntoAsync(
        SqliteConnection connection, string conversationId, IReadOnlyList<ChatMessage> messages,
        long? onlyMessageId = null,
        long? minimumMessageId = null,
        long? maximumMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0) return;

        // Index by id so the reader below is a dictionary hit per attachment rather than a
        // scan of the message list — this runs over a whole conversation on every open.
        var byId = new Dictionary<long, ChatMessage>();
        foreach (var m in messages) byId[m.Id] = m;

        await using var command = connection.CreateCommand();
        command.CommandText = onlyMessageId is not null
            ? """
                SELECT message_id, id, kind, file_name, mime_type, size_bytes,
                       local_cache_path, remote_uri, status
                FROM message_attachments
                WHERE conversation_id = $conversation_id AND message_id = $message_id
                ORDER BY rowid;
                """
            : minimumMessageId is not null && maximumMessageId is not null
                ? """
                    SELECT message_id, id, kind, file_name, mime_type, size_bytes,
                           local_cache_path, remote_uri, status
                    FROM message_attachments
                    WHERE conversation_id = $conversation_id
                      AND message_id BETWEEN $minimum_message_id AND $maximum_message_id
                    ORDER BY message_id, rowid;
                    """
                : """
                SELECT message_id, id, kind, file_name, mime_type, size_bytes,
                       local_cache_path, remote_uri, status
                FROM message_attachments
                WHERE conversation_id = $conversation_id
                ORDER BY message_id, rowid;
                """;
        AppDatabase.Add(command, "$conversation_id", conversationId);
        if (onlyMessageId is { } scopedMessageId) AppDatabase.Add(command, "$message_id", scopedMessageId);
        if (minimumMessageId is { } minimum) AppDatabase.Add(command, "$minimum_message_id", minimum);
        if (maximumMessageId is { } maximum) AppDatabase.Add(command, "$maximum_message_id", maximum);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var messageId = reader.GetInt64(0);
            // An attachment whose message wasn't in the requested set is skipped rather than
            // treated as an error — expected whenever this is called with a scoped message
            // list, and harmless otherwise.
            if (!byId.TryGetValue(messageId, out var message)) continue;

            message.Attachments.Add(new ChatAttachment
            {
                Id = reader.GetString(1),
                Kind = reader.GetString(2) == "image" ? AttachmentKind.Image : AttachmentKind.File,
                FileName = reader.GetString(3),
                MimeType = ReadNullableString(reader, 4),
                SizeBytes = reader.GetInt64(5),
                LocalCachePath = ReadNullableString(reader, 6),
                RemoteUri = ReadNullableString(reader, 7),
                Status = ReadNullableString(reader, 8) == "failed" ? AttachmentStatus.Failed : AttachmentStatus.Sent,
            });
        }
    }

    /// <summary>
    /// The bare file names of every cached attachment the database still references, across all
    /// conversations. Feeds the image-cache orphan sweep: anything in the cache directory that is
    /// not in this set belongs to a conversation that has since been deleted.
    /// <para>File names rather than full paths on purpose — the same install can produce
    /// different absolute roots (unpackaged writes to <c>%AppData%</c>, packaged to the MSIX
    /// LocalState), and comparing full paths would classify every row written under the other
    /// root as unreferenced and delete images that are very much still in use. The names are
    /// content hashes, so they are unique on their own.</para>
    /// <para>Returns <b>null</b> if the query fails — deliberately not an empty set, which is a
    /// legitimate answer meaning "no conversation references any image" (the user deleted them
    /// all) and would correctly make every cached file an orphan. A failed read must not be
    /// mistaken for that; <c>ImageCachePruner</c> deletes nothing when it gets null.</para>
    /// </summary>
    public async Task<IReadOnlySet<string>?> GetReferencedCacheFileNamesAsync(CancellationToken ct = default)
    {
        // Windows file names are case-insensitive; a comparison that isn't would see a
        // re-cased path as an orphan and delete a live image.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var connection = await AppDatabase.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT DISTINCT local_cache_path FROM message_attachments WHERE local_cache_path IS NOT NULL;";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var path = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (string.IsNullOrWhiteSpace(path)) continue;
                try { names.Add(Path.GetFileName(path)); }
                catch (ArgumentException) { /* malformed stored path — nothing to protect */ }
            }
        }
        catch (Exception ex)
        {
            LogFailure(nameof(GetReferencedCacheFileNamesAsync), "*", ex);
            // Signal "unknown" rather than "nothing is referenced": the set read so far may be
            // partial, and every row we failed to reach would look like an orphan.
            return null;
        }
        return names;
    }

    /// <summary>
    /// The id to give the next bubble appended to this conversation, without reading the
    /// conversation itself. Callers that only need to append (rather than reconcile the whole
    /// turn) have no reason to pull every row across just to take a maximum.
    /// </summary>
    public async Task<long> GetNextMessageIdAsync(string conversationId)
    {
        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(id), 0) + 1 FROM messages WHERE conversation_id = $conversation_id;";
            AppDatabase.Add(command, "$conversation_id", conversationId);
            var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
            return value is long next && next > 0 ? next : 1;
        }
        catch (Exception ex)
        {
            // Falling back to 1 on a conversation that already has rows would collide with the
            // primary key and lose the append — worth finding in the log rather than guessing at.
            LogFailure(nameof(GetNextMessageIdAsync), conversationId, ex);
            return 1;
        }
    }

    /// <summary>
    /// Finds conversations whose transcript contains <paramref name="query"/>, returning one
    /// excerpt per conversation (the most recent matching message).
    ///
    /// <para>This is the only path in the app that searches what was actually <i>said</i>. The
    /// sidebar's conversation search matches titles and previews, and Ctrl+F only searches the
    /// page already open, so "find the chat where we discussed the migration" had no answer
    /// unless those words happened to be in the first message that became the title.</para>
    ///
    /// <para>Restricted to <c>user</c> and <c>agent</c> rows on purpose: the same table holds
    /// tool arguments, tool output and process narration, and matching those would return
    /// conversations on the strength of a file path scrolling past inside a tool card. The excerpt
    /// is the newest hit within each conversation, and <paramref name="limit"/> caps how many
    /// conversations come back.</para>
    ///
    /// <para><paramref name="limit"/> bounds the <i>result</i>, not the work — every matching row
    /// has to be seen before the newest one per conversation is known. Keeping that pass cheap is
    /// what the query's shape is for; see the note on the SQL below.</para>
    /// </summary>
    /// <returns>Conversation id → excerpt. Empty on a blank query or on failure; this backs a
    /// search box, so a broken query degrades to "no content matches" rather than an error.</returns>
    public async Task<IReadOnlyDictionary<string, string>> SearchMessageContentAsync(
        string query,
        int limit = 200,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(query)) return results;
        var normalizedQuery = query.Trim();
        // FTS5's trigram tokenizer needs at least three characters. Shorter input still filters
        // the in-memory session/agent catalog instantly, but does not justify a transcript scan.
        if (normalizedQuery.Length < 3) return results;

        try
        {
            await using var connection = await AppDatabase.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            // Aggregate on the match set alone, then join `messages` once per surviving
            // conversation. The previous form ranked with ROW_NUMBER() OVER (PARTITION BY ...),
            // which had to join `messages` for *every matched row* and then sort the whole match
            // set before the LIMIT could discard any of it — so the LIMIT bounded the result but
            // not the work, and the trigram tokenizer makes broad short queries match nearly
            // everything. Measured on a 60k-message database: 91 ms -> 27 ms for a three-letter
            // query, 105 ms -> 49 ms for a word, with byte-identical results.
            //
            // MAX(message_id) selects the same row the ORDER BY did: within a conversation, ids
            // are assigned MAX(id)+1, so id order is message order and created_at only ever ties
            // where the old ordering fell through to id anyway.
            command.CommandText = """
                WITH hits(conversation_id, message_id) AS (
                    SELECT conversation_id, message_id
                    FROM message_search
                    WHERE message_search MATCH $match
                ),
                newest(conversation_id, message_id) AS (
                    SELECT conversation_id, MAX(message_id)
                    FROM hits
                    GROUP BY conversation_id
                )
                SELECT newest.conversation_id, messages.content
                FROM newest
                JOIN messages ON messages.conversation_id = newest.conversation_id
                             AND messages.id = newest.message_id
                ORDER BY messages.created_at DESC
                LIMIT $limit;
                """;
            AppDatabase.Add(command, "$match", $"\"{normalizedQuery.Replace("\"", "\"\"")}\"");
            AppDatabase.Add(command, "$limit", limit);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results[reader.GetString(0)] = reader.GetString(1);
            }
        }
        catch (OperationCanceledException)
        {
            // The user typed another character; this query's results are already stale.
            return results;
        }
        catch (Exception ex)
        {
            LogFailure(nameof(SearchMessageContentAsync), query, ex);
        }

        return results;
    }

    /// <summary>
    /// The conversation's most recent agent bubble (with its attachments), or null if it has
    /// none yet. This is the only pre-existing row a turn's projection can still touch — an
    /// <c>agent_image</c> event hangs its attachment on the last agent bubble, and the final
    /// reply is deduped against it — so loading just this row is what lets a turn be persisted
    /// without reading the whole conversation back first.
    /// </summary>
    /// <summary>
    /// The newest message in the conversation regardless of role, without its attachments.
    /// <para>Used as the alignment check when recovering a turn the host finished while the app
    /// was closed: the recovery is only safe if the local transcript still ends exactly at the
    /// user message that started the turn, which is what proves nothing of that turn was
    /// persisted and no rows would be duplicated by appending its tail.</para>
    /// </summary>
    public async Task<ChatMessage?> LoadLastMessageAsync(string conversationId)
    {
        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, role, content, agent_name,
                       event_kind, event_key, event_eyebrow, event_title,
                       event_detail, event_meta, event_args, event_result,
                       event_status, is_onboarding, created_at
                FROM messages
                WHERE conversation_id = $conversation_id
                ORDER BY id DESC
                LIMIT 1;
                """;
            AppDatabase.Add(command, "$conversation_id", conversationId);

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            return await reader.ReadAsync().ConfigureAwait(false) ? RowToMessage(reader) : null;
        }
        catch (Exception ex)
        {
            LogFailure(nameof(LoadLastMessageAsync), conversationId, ex);
            return null;
        }
    }

    /// <summary>
    /// Loads the newest visible bubble for every requested conversation in one connection and
    /// one SQL statement. This is the sidebar preview path; issuing one query per row makes a
    /// navigation refresh scale with the number of conversations.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ChatMessage>> LoadLastMessagesAsync(
        IReadOnlyCollection<string> conversationIds,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
        if (conversationIds.Count == 0) return results;

        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Grouped, not correlated. The obvious form of this query puts the MAX(id) in a scalar
        // subquery beside `id = (...)`, but that subquery references the outer row's
        // conversation_id, so SQLite marks it CORRELATED SCALAR SUBQUERY and re-evaluates it for
        // *every message row* in every requested conversation — the cost becomes O(messages),
        // not O(conversations), which on the first sidebar refresh after launch (nothing cached,
        // so every conversation is requested) means walking the whole messages table.
        //
        // Resolving the ids in their own GROUP BY first makes that one index scan per
        // conversation, and the join back is then a primary-key seek per row. Measured on a
        // 20k-message database: 14 ms correlated, 4 ms grouped.
        command.CommandText = """
            WITH requested(conversation_id) AS (
                SELECT value FROM json_each($conversation_ids)
            ),
            newest(conversation_id, id) AS (
                SELECT conversation_id, MAX(id)
                FROM messages
                WHERE conversation_id IN (SELECT conversation_id FROM requested)
                  AND event_kind IS NOT 'approval'
                GROUP BY conversation_id
            )
            SELECT messages.id, role, content, agent_name,
                   event_kind, event_key, event_eyebrow, event_title,
                   event_detail, event_meta, event_args, event_result,
                   event_status, is_onboarding, created_at, messages.conversation_id
            FROM messages
            JOIN newest ON newest.conversation_id = messages.conversation_id
                       AND newest.id = messages.id;
            """;
        var ids = conversationIds.Distinct(StringComparer.Ordinal).ToList();
        AppDatabase.Add(
            command,
            "$conversation_ids",
            JsonSerializer.Serialize(ids, AppJsonContext.Default.ListString));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results[reader.GetString(15)] = RowToMessage(reader);
        }
        return results;
    }

    public async Task<ChatMessage?> LoadLastAgentMessageAsync(string conversationId)
    {
        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, role, content, agent_name,
                       event_kind, event_key, event_eyebrow, event_title,
                       event_detail, event_meta, event_args, event_result,
                       event_status, is_onboarding, created_at
                FROM messages
                WHERE conversation_id = $conversation_id AND role = 'agent'
                ORDER BY id DESC
                LIMIT 1;
                """;
            AppDatabase.Add(command, "$conversation_id", conversationId);

            ChatMessage? message = null;
            await using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
            {
                if (await reader.ReadAsync().ConfigureAwait(false)) message = RowToMessage(reader);
            }
            if (message is null) return null;

            await LoadAttachmentsIntoAsync(connection, conversationId, new[] { message }, message.Id)
                .ConfigureAwait(false);
            return message;
        }
        catch (Exception ex)
        {
            LogFailure(nameof(LoadLastAgentMessageAsync), conversationId, ex);
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="messages"/> — and only those rows — into the conversation, inserting
    /// the ones that are new and updating the ones already there. Nothing else in the conversation
    /// is read, deleted, or rewritten, so the cost of persisting a turn scales with the size of the
    /// turn rather than the length of the conversation.
    ///
    /// Each written row's attachments are replaced wholesale (a single indexed delete plus its own
    /// inserts): an attachment's id is content-derived rather than stable, and an existing agent
    /// bubble can gain an image mid-turn, so diffing them per attachment would cost more than
    /// rewriting the handful a bubble has. <c>created_at</c> is deliberately left alone on update —
    /// a row keeps the timestamp it was first written with.
    /// </summary>
    public async Task UpsertMessagesAsync(string conversationId, IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0) return;

        await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);

        // One timestamp for the whole batch: a turn's bubbles are conceptually written at
        // one instant, and sharing it keeps their created_at ordering consistent with
        // their id ordering.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Commands are built once and re-bound per row inside the loop. Rebuilding them
        // per message would re-prepare the same SQL for every bubble in the turn.
        await using var upsertMessage = CreateMessageUpsert(connection, transaction);
        await using var clearAttachments = CreateAttachmentDelete(connection, transaction);
        await using var insertAttachment = CreateAttachmentInsert(connection, transaction);

        foreach (var message in messages)
        {
            BindMessage(upsertMessage, message, conversationId, now);
            await upsertMessage.ExecuteNonQueryAsync().ConfigureAwait(false);

            clearAttachments.Parameters["$conversation_id"].Value = conversationId;
            clearAttachments.Parameters["$message_id"].Value = message.Id;
            await clearAttachments.ExecuteNonQueryAsync().ConfigureAwait(false);

            foreach (var attachment in message.Attachments)
            {
                BindAttachment(insertAttachment, attachment, conversationId, message.Id, now);
                await insertAttachment.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        // A transcript is durable state, not a cache. Let a failed commit reach the run registry
        // so it reports a failed turn and never publishes Completed for history that is not on
        // disk. The transaction disposal rolls the whole batch back on any exception.
        await transaction.CommitAsync().ConfigureAwait(false);
        // The sidebar draws each row's preview from the newest message, so a transcript write is
        // one of the things StorageRevision has to account for. This runs only after commit, so a
        // failed write neither advances the revision nor looks successfully persisted.
        StorageRevision.Bump();
    }

    /// <summary>
    /// Drops a conversation's bubbles and their attachments.
    ///
    /// <b>The statement order is load-bearing.</b> The FKs in this schema declare no
    /// <c>ON DELETE</c> behavior, so nothing cascades — children must go before parents by
    /// hand. Reversing these two lines raises a constraint violation. That failure deliberately
    /// reaches the caller: transcript deletion is durable user state, not best-effort cleanup.
    /// The same applies one level up in <c>SessionRepository</c>: this method and
    /// <c>DeleteExecutionsAndTracesAsync</c> both have to run before the <c>sessions</c> row.
    /// </summary>
    public async Task DeleteMessagesAsync(string conversationId)
    {
        await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Attachments reference messages, so they go first.
        command.CommandText = """
            DELETE FROM message_attachments WHERE conversation_id = $conversation_id;
            DELETE FROM messages WHERE conversation_id = $conversation_id;
            """;
        AppDatabase.Add(command, "$conversation_id", conversationId);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Drops one bubble and its attachments. Used to un-send a message the user cancelled before
    /// it reached the agent — the row is written up front (so a page opening mid-turn sees it),
    /// which means backing out has to remove it again.
    ///
    /// Same ordering rule as <see cref="DeleteMessagesAsync"/>: nothing cascades, so the
    /// attachments go first.
    /// </summary>
    public async Task DeleteMessageAsync(string conversationId, long messageId)
    {
        await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM message_attachments
                WHERE conversation_id = $conversation_id AND message_id = $message_id;
            DELETE FROM messages
                WHERE conversation_id = $conversation_id AND id = $message_id;
            """;
        AppDatabase.Add(command, "$conversation_id", conversationId);
        AppDatabase.Add(command, "$message_id", messageId);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Drops a single execution and its trace rows — the counterpart to
    /// <see cref="InsertExecutionAsync"/> for a run that was cancelled before it ever reached the
    /// agent, so no record of it should survive. Traces first, for the reason above.
    /// </summary>
    public async Task DeleteExecutionAsync(string runId)
    {
        await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM trace_events WHERE execution_id = $run_id;
            DELETE FROM executions WHERE id = $run_id;
            """;
        AppDatabase.Add(command, "$run_id", runId);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    // --- helpers ---

    /// <summary>
    /// Logs failures on explicitly best-effort read/diagnostic paths. Durable transcript and
    /// execution mutations do not call this helper: their exceptions must reach the coordinator
    /// so it cannot publish success for state that SQLite did not commit.
    /// </summary>
    private void LogFailure(string operation, string conversationId, Exception ex)
    {
        LogDatabaseFailure(_logger, operation, conversationId, ex);
    }


    // ---- executions ----

    public async Task InsertExecutionAsync(
        string executionId, string conversationId, string? remoteSessionId,
        string prompt, string status)
    {
        await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO executions (id, conversation_id, remote_session_id, prompt, status, created_at)
            VALUES ($id, $conv, $remote, $prompt, $status, $ts);
            """;
        AppDatabase.Add(cmd, "$id", executionId);
        AppDatabase.Add(cmd, "$conv", conversationId);
        AppDatabase.Add(cmd, "$remote", remoteSessionId ?? (object)DBNull.Value);
        AppDatabase.Add(cmd, "$prompt", prompt);
        AppDatabase.Add(cmd, "$status", status);
        AppDatabase.Add(cmd, "$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>An execution row that was started but never finalized.</summary>
    public readonly record struct UnfinishedExecution(string ExecutionId, string Prompt, string? RemoteSessionId);

    /// <summary>
    /// The most recent turn in this conversation that was started and never reached a terminal
    /// state, or null if the conversation is settled.
    ///
    /// <para>This is the only durable trace a turn leaves before it finishes. The row is written
    /// with <c>status = 'running'</c> as the INPUT goes out and only rewritten by
    /// <see cref="FinalizeExecutionAsync"/>, while the turn's bubbles and trace events are
    /// written in one batch at the end — so a process killed mid-turn leaves exactly this row
    /// plus the user's own message, and nothing else. That makes it the marker for "the host may
    /// still be running this", which is what the resume-on-open probe keys off.</para>
    ///
    /// <para>Ordered by <c>created_at DESC</c> and limited to one: a conversation can only have
    /// one turn in flight, so an older unfinished row is debris from a previous crash and
    /// resuming it would attach to a session the host retired long ago.</para>
    /// </summary>
    public async Task<UnfinishedExecution?> GetUnfinishedExecutionAsync(string conversationId)
    {
        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, prompt, remote_session_id
                FROM executions
                WHERE conversation_id = $conv AND status = 'running'
                ORDER BY created_at DESC
                LIMIT 1;
                """;
            AppDatabase.Add(cmd, "$conv", conversationId);

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false)) return null;

            return new UnfinishedExecution(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        catch (Exception ex)
        {
            LogFailure(nameof(GetUnfinishedExecutionAsync), conversationId, ex);
            return null;
        }
    }

    public async Task FinalizeExecutionAsync(
        string executionId, string conversationId, string result, string status, double durationMs)
    {
        await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO executions
                (id, conversation_id, remote_session_id, prompt, result, status, duration_ms, created_at)
            VALUES
                ($id, $conv, NULL, '', $result, $status, $dur, $ts)
            ON CONFLICT(id) DO UPDATE SET
                result = excluded.result,
                status = excluded.status,
                duration_ms = excluded.duration_ms
            WHERE executions.conversation_id = excluded.conversation_id;
            """;
        AppDatabase.Add(cmd, "$result", result);
        AppDatabase.Add(cmd, "$status", status);
        AppDatabase.Add(cmd, "$dur", durationMs);
        AppDatabase.Add(cmd, "$id", executionId);
        AppDatabase.Add(cmd, "$conv", conversationId);
        AppDatabase.Add(cmd, "$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var changed = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidOperationException(
                $"Execution '{executionId}' does not belong to conversation '{conversationId}'.");
        }
    }

    // ---- trace events ----

    /// <summary>One trace row, as handed to <see cref="InsertTraceEventsAsync"/>.</summary>
    public readonly record struct TraceEventRow(
        string EventId, string? ExecutionId, string? SessionId,
        string Type, string PayloadJson, double? Ts);

    /// <summary>
    /// Writes a turn's whole trace in one connection and one transaction, reusing a single
    /// prepared command. A turn emits dozens of events, so the previous row-at-a-time API meant
    /// dozens of connection acquisitions and dozens of implicit commits.
    /// </summary>
    public async Task InsertTraceEventsAsync(string conversationId, IReadOnlyList<TraceEventRow> events)
    {
        if (events.Count == 0) return;

        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT OR REPLACE INTO trace_events (id, conversation_id, execution_id, session_id, type, payload_json, ts)
                VALUES ($id, $conv, $exec, $sess, $type, $payload, $ts);
                """;

            var id = cmd.Parameters.Add("$id", SqliteType.Text);
            cmd.Parameters.AddWithValue("$conv", conversationId);
            var exec = cmd.Parameters.Add("$exec", SqliteType.Text);
            var sess = cmd.Parameters.Add("$sess", SqliteType.Text);
            var type = cmd.Parameters.Add("$type", SqliteType.Text);
            var payload = cmd.Parameters.Add("$payload", SqliteType.Text);
            var ts = cmd.Parameters.Add("$ts", SqliteType.Real);
            cmd.Prepare();

            foreach (var e in events)
            {
                id.Value = e.EventId;
                exec.Value = e.ExecutionId ?? (object)DBNull.Value;
                sess.Value = e.SessionId ?? (object)DBNull.Value;
                type.Value = e.Type;
                payload.Value = e.PayloadJson;
                ts.Value = e.Ts ?? (object)DBNull.Value;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailure(nameof(InsertTraceEventsAsync), conversationId, ex);
        }
    }

    public async Task DeleteExecutionsAndTracesAsync(string conversationId)
    {
        await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            DELETE FROM trace_events WHERE conversation_id = $conv;
            DELETE FROM executions WHERE conversation_id = $conv;
            """;
        AppDatabase.Add(cmd, "$conv", conversationId);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }
}
