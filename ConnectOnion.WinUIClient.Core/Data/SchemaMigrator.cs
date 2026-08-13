using System.Text.Json;
using ConnectOnion.WinUIClient.Models;
using Microsoft.Data.Sqlite;

namespace ConnectOnion.WinUIClient.Data;

/// <summary>
/// Versions the local database and upgrades it forward, one step at a time.
///
/// Before this existed the schema was created purely with <c>CREATE TABLE IF NOT EXISTS</c>, which
/// silently does nothing when a table already exists but has the wrong shape — so adding a column
/// meant every existing install either crashed or quietly lost the new field, and the documented
/// recovery was "delete the .db file". That is fine in development and unacceptable once anyone
/// has data worth keeping.
///
/// The version lives in SQLite's own <c>PRAGMA user_version</c> (an integer in the DB header)
/// rather than a row in <c>app_meta</c>: it needs no table to exist first, costs no query, and is
/// written atomically inside the same transaction as the migration it records.
///
/// <b>To add a migration:</b> append one entry to <see cref="Migrations"/> with the next version
/// number and bump <see cref="LatestVersion"/>. Never edit or renumber a released migration —
/// databases in the wild have already recorded that they ran it.
/// </summary>
internal static class SchemaMigrator
{
    /// <summary>
    /// Version 1 is the baseline: the schema produced by <see cref="AppDatabase.CreateSchemaAsync"/>.
    /// An unversioned database is repaired to that shape and any legacy conversation envelopes
    /// are imported before it is stamped as version 1 (see <see cref="ApplyAsync"/>).
    /// </summary>
    private const int BaselineVersion = 1;

    /// <summary>The schema version this build expects. Bump when you append to <see cref="Migrations"/>.</summary>
    internal const int LatestVersion = 12;

    /// <summary>
    /// Ordered forward migrations. Each runs exactly once, in its own transaction, on databases
    /// whose recorded version is below it.
    /// </summary>
    private static readonly IReadOnlyList<Migration> Migrations = new Migration[]
    {
        // v2 — token usage ledger.
        //
        // Deliberately has NO foreign key to sessions, and is never touched by conversation or
        // agent deletion. Usage is a *ledger*: it records that tokens were spent, and that stays
        // true after the conversation is gone. A cascade here would make the totals silently
        // shrink whenever a user tidies up their chat list, and a totals panel that changes when
        // you delete things cannot answer any question honestly.
        //
        // conversation_id / agent_id are therefore plain (possibly dangling) references kept only
        // for drill-down, and agent_name is snapshotted at write time so a deleted agent's rows can
        // still be labelled. Clearing usage is its own explicit user action — see UsageRepository.
        new(2, """
            CREATE TABLE IF NOT EXISTS usage_events (
                id                 TEXT PRIMARY KEY,
                conversation_id    TEXT,
                agent_id           TEXT,
                agent_name         TEXT,
                model              TEXT NOT NULL,
                input_tokens       INTEGER NOT NULL DEFAULT 0,
                output_tokens      INTEGER NOT NULL DEFAULT 0,
                cached_tokens      INTEGER NOT NULL DEFAULT 0,
                cache_write_tokens INTEGER NOT NULL DEFAULT 0,
                duration_ms        REAL,
                created_at         INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_usage_created ON usage_events(created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_usage_model_created ON usage_events(model, created_at DESC);
            """),

        // v3 — per-conversation approval mode (safe / accept_edits / plan).
        //
        // The mode has to live here rather than be read back from the agent: the host keeps its
        // own copy in the session it stores, but a client cannot reliably set that copy (its
        // session merge prefers whichever side has run more iterations, and we never report any),
        // and it tells us the mode only via a mode_changed event on a live socket. So the
        // conversation's mode is ours to remember and re-assert on every turn.
        new(3, """
            ALTER TABLE sessions ADD COLUMN mode TEXT NOT NULL DEFAULT 'safe';
            """),

        // v4 — repair and prevent sessions whose owning agent no longer exists.
        //
        // Older builds deleted the agent row before tearing down its conversations. If any child
        // cleanup then failed, the session row survived with no owner: the sidebar hid it because
        // it builds under existing agents, while global surfaces such as the tray still found it.
        // Clean the full conversation graph in the same child-before-parent order as the runtime
        // delete path. usage_events is intentionally untouched — it is an immutable spend ledger.
        //
        // Rebuilding `sessions` merely to add a foreign key would be risky because four existing
        // tables already reference it. These guards provide the same integrity boundary without a
        // table rebuild: a session cannot name a missing agent, and an agent cannot disappear
        // while sessions still belong to it.
        new(4, """
            DELETE FROM message_attachments
            WHERE conversation_id IN (
                SELECT sessions.id
                FROM sessions
                LEFT JOIN agents ON agents.id = sessions.agent_id
                WHERE agents.id IS NULL
            );

            DELETE FROM messages
            WHERE conversation_id IN (
                SELECT sessions.id
                FROM sessions
                LEFT JOIN agents ON agents.id = sessions.agent_id
                WHERE agents.id IS NULL
            );

            DELETE FROM trace_events
            WHERE conversation_id IN (
                SELECT sessions.id
                FROM sessions
                LEFT JOIN agents ON agents.id = sessions.agent_id
                WHERE agents.id IS NULL
            );

            DELETE FROM executions
            WHERE conversation_id IN (
                SELECT sessions.id
                FROM sessions
                LEFT JOIN agents ON agents.id = sessions.agent_id
                WHERE agents.id IS NULL
            );

            DELETE FROM sessions
            WHERE NOT EXISTS (
                SELECT 1 FROM agents WHERE agents.id = sessions.agent_id
            );

            DELETE FROM app_meta
            WHERE key = 'active_session_id'
              AND NOT EXISTS (
                  SELECT 1 FROM sessions WHERE sessions.id = app_meta.value
              );

            CREATE TRIGGER IF NOT EXISTS sessions_agent_insert_guard
            BEFORE INSERT ON sessions
            FOR EACH ROW
            WHEN NOT EXISTS (SELECT 1 FROM agents WHERE agents.id = NEW.agent_id)
            BEGIN
                SELECT RAISE(ABORT, 'session references a missing agent');
            END;

            CREATE TRIGGER IF NOT EXISTS sessions_agent_update_guard
            BEFORE UPDATE OF agent_id ON sessions
            FOR EACH ROW
            WHEN NOT EXISTS (SELECT 1 FROM agents WHERE agents.id = NEW.agent_id)
            BEGIN
                SELECT RAISE(ABORT, 'session references a missing agent');
            END;

            CREATE TRIGGER IF NOT EXISTS agents_delete_guard
            BEFORE DELETE ON agents
            FOR EACH ROW
            WHEN EXISTS (SELECT 1 FROM sessions WHERE sessions.agent_id = OLD.id)
            BEGIN
                SELECT RAISE(ABORT, 'delete agent sessions before deleting the agent');
            END;
            """),

        // v5 — the BIP39 recovery phrase for the local identity.
        //
        // Nullable on purpose, and it will stay null forever for anyone who installed before this:
        // their seed came from a raw CSPRNG draw, and no phrase encodes it (BIP39 runs the other
        // way — phrase to seed). Those identities are not regenerated, because that would change
        // the address and every agent that authorized it; they simply export the raw seed instead.
        // Identities minted from here on are derived from a phrase, so they can be restored on
        // another machine or in any other ConnectOnion client.
        //
        // Stored the same way private_seed is: DPAPI-protected (CurrentUser) then base64. The
        // phrase and the seed are equivalent secrets — one derives the other — so protecting the
        // seed while writing the phrase in the clear right beside it would defeat the point.
        new(5, """
            ALTER TABLE identity_keys ADD COLUMN mnemonic TEXT NULL;
            """),

        // v6 — the user's chosen icon for a saved agent.
        //
        // Holds a path relative to the data root (avatars/agent-….png), never the image bytes and
        // never an absolute path: a BLOB would put a 256×256 PNG on every agent read, and an
        // absolute path breaks the moment a portable install is moved. Nullable on purpose —
        // "no custom icon" is the normal state, and those agents keep the name-initial avatar.
        new(6, """
            ALTER TABLE agents ADD COLUMN icon_path TEXT NULL;
            """),

        // v7 — "has this conversation's title been settled yet?" as a column instead of a regex.
        //
        // A new conversation is created as "Conversation N" and the first thing the user sends
        // replaces that with the message's opening words. Deciding *whether* a title is still the
        // placeholder used to be a `^Conversation \d+$` match against the title text, which meant
        // the placeholder could never be translated: the moment a Chinese install wrote "对话 1",
        // the pattern stopped matching and that conversation kept its placeholder name forever.
        // The same trap blocked letting the user rename a chat — a rename that happened to look
        // like "Conversation 4" would have been silently overwritten by the next message.
        //
        // The backfill reproduces the old predicate exactly so existing rows keep their current
        // behaviour: generic ⇔ "Conversation " followed by digits and nothing else. The first GLOB
        // requires a digit after the space; the second rejects anything with a non-digit anywhere
        // after it (so "Conversation 12" stays generic and "Conversation 1 draft" does not).
        new(7, """
            ALTER TABLE sessions ADD COLUMN has_custom_title INTEGER NOT NULL DEFAULT 0;

            UPDATE sessions
            SET has_custom_title = 1
            WHERE NOT (title GLOB 'Conversation [0-9]*'
                       AND title NOT GLOB 'Conversation *[^0-9]*');
            """),

        // v8 - trigram full-text index for global transcript search. The Windows winsqlite3
        // provider includes FTS5; trigram tokenization preserves the product's substring-search
        // behaviour while avoiding a leading-wildcard scan over the messages table.
        new(8, """
            CREATE VIRTUAL TABLE IF NOT EXISTS message_search
            USING fts5(conversation_id UNINDEXED, message_id UNINDEXED, content, tokenize='trigram');

            INSERT INTO message_search(conversation_id, message_id, content)
            SELECT conversation_id, id, content
            FROM messages
            WHERE role IN ('user', 'agent')
              AND content IS NOT NULL
              AND content <> '';

            CREATE TRIGGER IF NOT EXISTS message_search_insert
            AFTER INSERT ON messages
            WHEN NEW.role IN ('user', 'agent') AND NEW.content IS NOT NULL AND NEW.content <> ''
            BEGIN
                INSERT INTO message_search(conversation_id, message_id, content)
                VALUES (NEW.conversation_id, NEW.id, NEW.content);
            END;

            CREATE TRIGGER IF NOT EXISTS message_search_delete
            AFTER DELETE ON messages
            BEGIN
                DELETE FROM message_search
                WHERE conversation_id = OLD.conversation_id AND message_id = OLD.id;
            END;

            CREATE TRIGGER IF NOT EXISTS message_search_update
            AFTER UPDATE OF id, conversation_id, role, content ON messages
            BEGIN
                DELETE FROM message_search
                WHERE conversation_id = OLD.conversation_id AND message_id = OLD.id;
                INSERT INTO message_search(conversation_id, message_id, content)
                SELECT NEW.conversation_id, NEW.id, NEW.content
                WHERE NEW.role IN ('user', 'agent')
                  AND NEW.content IS NOT NULL
                  AND NEW.content <> '';
            END;
            """),

        // v9 - make the search index's own maintenance indexed.
        //
        // v8's triggers located the row to remove with
        // `WHERE conversation_id = ... AND message_id = ...`. Both of those columns are declared
        // UNINDEXED on the FTS5 table, and a non-MATCH constraint on an fts5 virtual table has no
        // index to use at all - EXPLAIN QUERY PLAN reports `SCAN message_search VIRTUAL TABLE`.
        // So every removed message scanned the *entire* search index, and because the trigger is
        // per-row, `DELETE FROM messages WHERE conversation_id = ?` made deleting one conversation
        // O(its messages x every message in the database). Measured on a 20k-message database,
        // deleting a 500-message conversation took 1.03 s; the same delete after this migration
        // takes 0.003 s.
        //
        // The fix is to address the FTS row by its rowid, which fts5 *does* look up directly (the
        // plan becomes `... VIRTUAL TABLE INDEX 0:=`). `message_search_map` supplies that rowid and
        // is an ordinary table, so its (conversation_id, message_id) lookup is a real index seek.
        //
        // This is emphatically *not* the "couple the index to SQLite's mutable implicit rowid" trap
        // the search design warns about. The rowid stored here is the one we assign to the *FTS*
        // row ourselves, and the stable (conversation_id, message_id) pair remains the only thing
        // that identifies a message. Nothing reads `messages.rowid`.
        //
        // The index is rebuilt rather than migrated in place: v8 databases already hold FTS rows
        // whose rowids were assigned implicitly and recorded nowhere, so there is nothing to
        // back-fill the map from. Rebuilding also re-derives the role/content filter, which is why
        // the tool/system rows a v8 database never indexed stay out.
        new(9, """
            CREATE TABLE IF NOT EXISTS message_search_map (
                fts_rowid       INTEGER PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                message_id      INTEGER NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_message_search_map
                ON message_search_map(conversation_id, message_id);

            DROP TRIGGER IF EXISTS message_search_insert;
            DROP TRIGGER IF EXISTS message_search_delete;
            DROP TRIGGER IF EXISTS message_search_update;

            DELETE FROM message_search;
            DELETE FROM message_search_map;

            INSERT INTO message_search_map(conversation_id, message_id)
            SELECT conversation_id, id
            FROM messages
            WHERE role IN ('user', 'agent')
              AND content IS NOT NULL
              AND content <> '';

            INSERT INTO message_search(rowid, conversation_id, message_id, content)
            SELECT map.fts_rowid, map.conversation_id, map.message_id, messages.content
            FROM message_search_map AS map
            JOIN messages ON messages.conversation_id = map.conversation_id
                         AND messages.id = map.message_id;

            -- The map row is inserted first so last_insert_rowid() carries the id the FTS row is
            -- then given explicitly. Both statements share one condition, so the pair either both
            -- run or neither does and the two tables cannot drift apart.
            CREATE TRIGGER message_search_insert
            AFTER INSERT ON messages
            WHEN NEW.role IN ('user', 'agent') AND NEW.content IS NOT NULL AND NEW.content <> ''
            BEGIN
                INSERT INTO message_search_map(conversation_id, message_id)
                VALUES (NEW.conversation_id, NEW.id);

                INSERT INTO message_search(rowid, conversation_id, message_id, content)
                VALUES (last_insert_rowid(), NEW.conversation_id, NEW.id, NEW.content);
            END;

            CREATE TRIGGER message_search_delete
            AFTER DELETE ON messages
            BEGIN
                DELETE FROM message_search
                WHERE rowid = (
                    SELECT fts_rowid FROM message_search_map
                    WHERE conversation_id = OLD.conversation_id AND message_id = OLD.id);

                DELETE FROM message_search_map
                WHERE conversation_id = OLD.conversation_id AND message_id = OLD.id;
            END;

            -- Remove-then-reinsert, so a row that stops qualifying (its role edited away from
            -- user/agent, or its content cleared) leaves the index rather than sitting in it
            -- holding its old text.
            CREATE TRIGGER message_search_update
            AFTER UPDATE OF id, conversation_id, role, content ON messages
            BEGIN
                DELETE FROM message_search
                WHERE rowid = (
                    SELECT fts_rowid FROM message_search_map
                    WHERE conversation_id = OLD.conversation_id AND message_id = OLD.id);

                DELETE FROM message_search_map
                WHERE conversation_id = OLD.conversation_id AND message_id = OLD.id;

                INSERT INTO message_search_map(conversation_id, message_id)
                SELECT NEW.conversation_id, NEW.id
                WHERE NEW.role IN ('user', 'agent')
                  AND NEW.content IS NOT NULL
                  AND NEW.content <> '';

                INSERT INTO message_search(rowid, conversation_id, message_id, content)
                SELECT last_insert_rowid(), NEW.conversation_id, NEW.id, NEW.content
                WHERE NEW.role IN ('user', 'agent')
                  AND NEW.content IS NOT NULL
                  AND NEW.content <> '';
            END;
            """),

        // v10 - two indexes for deletes that were scanning.
        //
        // trace_events had only ix_trace_events_conversation(conversation_id, ts), but
        // ConversationRepository.DeleteExecutionAsync removes traces by execution_id - a full scan
        // on a user-facing action (cancelling a run before it reached the agent).
        //
        // sessions had only ix_sessions_agent_updated(agent_id, updated_at DESC), whose leading
        // column does not match SessionRepository.LoadAsync's
        // `ORDER BY sort_order, updated_at DESC, id`. That load runs on every sidebar refresh and
        // was paying a full table scan plus a temporary B-tree sort; this index covers the order
        // exactly, so the sort disappears.
        new(10, """
            CREATE INDEX IF NOT EXISTS ix_trace_events_execution
                ON trace_events(execution_id);

            CREATE INDEX IF NOT EXISTS ix_sessions_order
                ON sessions(sort_order, updated_at DESC, id);
            """),

        // v11 - persist sidebar attention independently of notification delivery. A row is
        // cleared when its conversation is opened; approval_required distinguishes an actionable
        // interruption from an ordinary unread reply.
        new(11, """
            ALTER TABLE sessions ADD COLUMN unread_count INTEGER NOT NULL DEFAULT 0
                CHECK(unread_count >= 0);
            ALTER TABLE sessions ADD COLUMN requires_attention INTEGER NOT NULL DEFAULT 0
                CHECK(requires_attention IN (0, 1));
            """),

        // v12 - remove agents.invite_code.
        //
        // The column shipped in the v1 baseline and was never populated by production code: the
        // only thing that ever assigned AgentConfig.InviteCode was the repository reading it back,
        // so it was always NULL. It round-tripped correctly and had a passing test for that, which
        // is precisely why nothing flagged that no caller wrote to it.
        //
        // Removed rather than left dormant because an invite code is a *trust credential*. Writing
        // one here in plaintext would put a usable secret in the same file whose other secret
        // (identity_keys.private_seed) is DPAPI-protected specifically so that copying the .db
        // gets an attacker nothing. Leaving a plausible-looking column named invite_code is an
        // invitation for someone to wire it up later without noticing that asymmetry. The live
        // value belongs in memory only, on AgentConnectionService.InviteCode, for the lifetime of
        // one connection.
        //
        // This is a table rebuild, not ALTER TABLE ... DROP COLUMN: that syntax needs SQLite
        // 3.35+, while the app supports Windows 10 1809 (TargetPlatformMinVersion 10.0.17763.0)
        // and cannot assume its system winsqlite3 has that feature. The rebuild works throughout
        // the supported OS range.
        //
        // Nothing holds a foreign key to `agents` — v4 deliberately chose triggers over an FK (see
        // its comment) — so there is no dependent-row enforcement to defer and DROP TABLE raises no
        // constraint violation. What v4 leaves instead is three triggers, and **all three have to
        // come down before the rebuild**, for two different reasons:
        //   * `agents_delete_guard` is BEFORE DELETE *on agents*, so DROP TABLE destroys it anyway.
        //     Dropping it explicitly keeps that from being a silent side effect.
        //   * `sessions_agent_insert_guard` and `sessions_agent_update_guard` live on `sessions`
        //     and survive — but their bodies name `agents`. Since SQLite 3.25 an ALTER TABLE RENAME
        //     re-parses the whole schema, and it fails outright ("error in trigger
        //     sessions_agent_insert_guard: no such table: main.agents") because at that moment the
        //     old table is gone and the new one has not been renamed into place yet. Leaving them
        //     up does not weaken the rebuild, it *aborts* it.
        // All three are recreated verbatim at the end, so the integrity boundary v4 established is
        // unchanged either side of this migration.
        //
        // The new table is built under a temporary name and renamed into place. The reverse
        // (renaming `agents` out of the way first) would be wrong: that same 3.25 behaviour
        // rewrites references to the renamed table, so it would edit the guards to point at the
        // temporary name.
        //
        // Column list is explicit and by name, so the physical order left by v6's
        // `ALTER TABLE agents ADD COLUMN icon_path` (appended last) does not matter; the rebuild
        // also takes the opportunity to put icon_path back in its logical position.
        new(12, """
            DROP TRIGGER IF EXISTS agents_delete_guard;
            DROP TRIGGER IF EXISTS sessions_agent_insert_guard;
            DROP TRIGGER IF EXISTS sessions_agent_update_guard;

            CREATE TABLE agents_v12 (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                address TEXT NOT NULL,
                direct_url TEXT NULL,
                icon_path TEXT NULL,
                info_json TEXT NULL,
                info_updated_at TEXT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0
            );

            INSERT INTO agents_v12 (
                id, name, address, direct_url, icon_path, info_json, info_updated_at, sort_order)
            SELECT
                id, name, address, direct_url, icon_path, info_json, info_updated_at, sort_order
            FROM agents;

            DROP TABLE agents;

            ALTER TABLE agents_v12 RENAME TO agents;

            CREATE TRIGGER sessions_agent_insert_guard
            BEFORE INSERT ON sessions
            FOR EACH ROW
            WHEN NOT EXISTS (SELECT 1 FROM agents WHERE agents.id = NEW.agent_id)
            BEGIN
                SELECT RAISE(ABORT, 'session references a missing agent');
            END;

            CREATE TRIGGER sessions_agent_update_guard
            BEFORE UPDATE OF agent_id ON sessions
            FOR EACH ROW
            WHEN NOT EXISTS (SELECT 1 FROM agents WHERE agents.id = NEW.agent_id)
            BEGIN
                SELECT RAISE(ABORT, 'session references a missing agent');
            END;

            CREATE TRIGGER agents_delete_guard
            BEFORE DELETE ON agents
            FOR EACH ROW
            WHEN EXISTS (SELECT 1 FROM sessions WHERE sessions.agent_id = OLD.id)
            BEGIN
                SELECT RAISE(ABORT, 'delete agent sessions before deleting the agent');
            END;
            """),
    };

    private sealed record Migration(int Version, string Sql);

    /// <summary>
    /// Brings <paramref name="connection"/> up to <see cref="LatestVersion"/>. Called by
    /// <see cref="AppDatabase"/> right after the baseline schema is ensured.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The database was written by a newer build than this one. We refuse rather than run against a
    /// shape we do not understand — a downgrade that "mostly works" is how data gets corrupted.
    /// </exception>
    internal static async Task ApplyAsync(SqliteConnection connection)
    {
        var current = await ReadVersionAsync(connection).ConfigureAwait(false);

        // 0 means "never versioned": a newly created database already has the baseline shape,
        // while older builds may be missing baseline columns or still store message history in
        // conversation envelopes. Repair/import inside one transaction, then stamp version 1.
        if (current == 0)
        {
            await UpgradeUnversionedBaselineAsync(connection).ConfigureAwait(false);
            current = BaselineVersion;
        }

        if (current > LatestVersion)
        {
            throw new InvalidOperationException(
                $"This database is at schema version {current}, but this build of ConnectOnion only " +
                $"understands version {LatestVersion}. It was most likely written by a newer version " +
                "of the app. Update ConnectOnion rather than continuing with this database.");
        }

        foreach (var migration in Migrations)
        {
            if (migration.Version <= current) continue;

            await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // PRAGMA user_version does not accept a parameter, and the value is an int constant
            // from our own migration list — never user input.
            await using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = (SqliteTransaction)transaction;
                stamp.CommandText = $"PRAGMA user_version = {migration.Version};";
                await stamp.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
            current = migration.Version;
        }
    }

    private static async Task UpgradeUnversionedBaselineAsync(SqliteConnection connection)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);

        await AddColumnIfMissingAsync(
            connection, transaction, "sessions", "remote_session_id", "TEXT NULL").ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection, transaction, "sessions", "last_processed_event_id", "TEXT NULL").ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection, transaction, "preferences", "microphone_device_id", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);

        if (await TableExistsAsync(connection, transaction, "conversations").ConfigureAwait(false))
            await ImportLegacyConversationsAsync(connection, transaction).ConfigureAwait(false);

        await using (var stamp = connection.CreateCommand())
        {
            stamp.Transaction = transaction;
            stamp.CommandText = $"PRAGMA user_version = {BaselineVersion};";
            await stamp.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static async Task ImportLegacyConversationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var rows = new List<(string SessionId, string EnvelopeJson)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT session_id, envelope_json FROM conversations ORDER BY storage_key;";
            await using var reader = await read.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
                rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var row in rows)
        {
            if (!await SessionExistsAsync(connection, transaction, row.SessionId).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Legacy conversation '{row.SessionId}' has no matching session row. " +
                    "The database was left unchanged so it can be repaired or backed up.");
            }

            LegacyConversationEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize(
                    row.EnvelopeJson, AppJsonContext.Default.LegacyConversationEnvelope)
                    ?? throw new JsonException("the envelope was null");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Legacy conversation '{row.SessionId}' could not be decoded. " +
                    "The database was left unchanged so it can be repaired or backed up.", ex);
            }

            var baseTimestamp = envelope.State.CreatedAt > 0
                ? envelope.State.CreatedAt
                : envelope.State.UpdatedAt;
            for (var index = 0; index < envelope.State.Messages.Count; index++)
            {
                var message = envelope.State.Messages[index];
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR IGNORE INTO messages
                      (id, conversation_id, role, content, agent_name,
                       event_kind, event_key, event_eyebrow, event_title,
                       event_detail, event_meta, event_args, event_result,
                       event_status, is_onboarding, created_at)
                    VALUES
                      ($id, $conversation_id, $role, $content, $agent_name,
                       $event_kind, NULL, $event_eyebrow, $event_title,
                       $event_detail, $event_meta, $event_args, $event_result,
                       $event_status, $is_onboarding, $created_at);
                    """;
                AppDatabase.Add(insert, "$id", message.Id);
                AppDatabase.Add(insert, "$conversation_id", row.SessionId);
                AppDatabase.Add(insert, "$role", message.Role switch
                {
                    ChatRole.Agent => "agent",
                    ChatRole.Event => "event",
                    _ => "user",
                });
                AppDatabase.Add(insert, "$content", message.Content);
                AppDatabase.Add(insert, "$agent_name", message.AgentName);
                AppDatabase.Add(insert, "$event_kind", message.EventKind);
                AppDatabase.Add(insert, "$event_eyebrow", message.EventEyebrow);
                AppDatabase.Add(insert, "$event_title", message.EventTitle);
                AppDatabase.Add(insert, "$event_detail", message.EventDetail);
                AppDatabase.Add(insert, "$event_meta", message.EventMeta);
                AppDatabase.Add(insert, "$event_args", message.EventArgs);
                AppDatabase.Add(insert, "$event_result", message.EventResult);
                AppDatabase.Add(insert, "$event_status", message.Status switch
                {
                    EventStatus.Running => "running",
                    EventStatus.Error => "error",
                    _ => "done",
                });
                AppDatabase.Add(insert, "$is_onboarding", message.IsOnboarding ? 1 : 0);
                AppDatabase.Add(insert, "$created_at", baseTimestamp + index);
                await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        await using var marker = connection.CreateCommand();
        marker.Transaction = transaction;
        marker.CommandText = """
            INSERT INTO app_meta (key, value)
            VALUES ('legacy_conversations_migrated', '1')
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        await marker.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string declaration)
    {
        if (!await TableExistsAsync(connection, transaction, table).ConfigureAwait(false)
            || await ColumnExistsAsync(connection, transaction, table, column).ConfigureAwait(false))
            return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // All three identifiers/declarations are constants owned by this file, never input.
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name;";
        command.Parameters.AddWithValue("$name", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<bool> SessionExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sessions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<int> ReadVersionAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

}
