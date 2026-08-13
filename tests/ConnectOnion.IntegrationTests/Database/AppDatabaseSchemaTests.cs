using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.IntegrationTests.Database;

[Collection(DatabaseCollection.Name)]
public sealed class AppDatabaseSchemaTests(TempDatabaseFixture fixture)
{
    [Fact]
    public async Task OpenAsync_CalledTwice_CreatesSchemaIdempotently()
    {
        await using (var first = await AppDatabase.OpenAsync())
            Assert.Equal(1, await ScalarLongAsync(first, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'messages';"));

        await using var second = await AppDatabase.OpenAsync();
        Assert.Equal(1, await ScalarLongAsync(second, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'message_attachments';"));
        Assert.True(File.Exists(fixture.DatabasePath));
    }

    [Fact]
    public async Task OpenAsync_NewDatabase_AppliesLatestMigration()
    {
        await using var connection = await AppDatabase.OpenAsync();

        // Asserted against the constant, not a literal: these three tests are about "reached the
        // latest version", so a new migration should not have to touch them to stay honest.
        Assert.Equal(SchemaMigrator.LatestVersion, (int)await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'usage_events';"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name = 'mode';"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('identity_keys') WHERE name = 'mnemonic';"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name = 'icon_path';"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name = 'unread_count';"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name = 'requires_attention';"));
    }

    /// <summary>
    /// Two indexes that exist for deletes and one ordering, none of which any query names
    /// explicitly — so nothing else in the suite would notice if a migration edit dropped them.
    ///
    /// <para><c>ix_trace_events_execution</c> backs
    /// <c>ConversationRepository.DeleteExecutionAsync</c>, which removes traces by
    /// <c>execution_id</c>; without it that user-facing action full-scans <c>trace_events</c>.
    /// <c>ix_sessions_order</c> covers <c>SessionRepository.LoadAsync</c>'s
    /// <c>ORDER BY sort_order, updated_at DESC, id</c> — the pre-existing
    /// <c>ix_sessions_agent_updated</c> leads with a different column, so that load ran a full scan
    /// plus a temporary B-tree sort on every sidebar refresh.</para>
    /// </summary>
    [Fact]
    public async Task OpenAsync_NewDatabase_CreatesTheIndexesBehindDeletesAndSessionOrdering()
    {
        await using var connection = await AppDatabase.OpenAsync();

        Assert.Equal(1, await ScalarLongAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_trace_events_execution';"));
        Assert.Equal(1, await ScalarLongAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_sessions_order';"));

        // The ordering index has to actually be chosen, not merely exist: a covering index that
        // the planner declines still leaves the sort in place, which is the cost being removed.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT id FROM sessions ORDER BY sort_order, updated_at DESC, id;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));

        Assert.Contains(plan, detail => detail.Contains("ix_sessions_order", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan, detail => detail.Contains("USE TEMP B-TREE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ApplyAsync_VersionFiveDatabase_AddsIconPathAndLeavesExistingAgentsWithoutOne()
    {
        await using var initialized = await AppDatabase.OpenAsync();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await AppDatabase.CreateSchemaAsync(connection);
        await ExecuteAsync(connection, """
            ALTER TABLE identity_keys ADD COLUMN mnemonic TEXT NULL;
            PRAGMA user_version = 5;

            INSERT INTO agents (id, name, address, sort_order)
            VALUES ('agent-existing', 'Existing agent', '0xabc', 0);
            """);

        await SchemaMigrator.ApplyAsync(connection);

        Assert.Equal(SchemaMigrator.LatestVersion, (int)await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name = 'icon_path';"));
        // "No custom icon" is the normal state for everyone who upgrades: the column is nullable
        // precisely so an existing agent keeps working with its name-initial avatar.
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM agents WHERE id = 'agent-existing' AND icon_path IS NULL;"));
    }

    [Fact]
    public async Task OpenAsync_Connection_EnablesWalAndForeignKeys()
    {
        await using var connection = await AppDatabase.OpenAsync();

        Assert.Equal("wal", await ScalarStringAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "PRAGMA foreign_keys;"));
    }

    [Fact]
    public async Task ApplyAsync_VersionZeroDatabase_UpgradesToLatestVersion()
    {
        await using var initialized = await AppDatabase.OpenAsync();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        // A version-0 database is a *pre-versioning* one: it has the full baseline schema, it just
        // never recorded a version. Migrating a bare empty database is not a case that exists (the
        // app always ensures the baseline first), and pretending it is would let a migration that
        // alters an existing table pass a test with no table to alter.
        await AppDatabase.CreateSchemaAsync(connection);

        await SchemaMigrator.ApplyAsync(connection);

        Assert.Equal(SchemaMigrator.LatestVersion, (int)await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'usage_events';"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name = 'mode';"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('identity_keys') WHERE name = 'mnemonic';"));
    }

    [Fact]
    public async Task ApplyAsync_PreMessagesDatabase_ImportsConversationEnvelopeAndRepairsBaselineColumns()
    {
        await using var initialized = await AppDatabase.OpenAsync();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        // Exact schema from the last blob-storage build (8015d33^). CreateSchemaAsync mirrors the
        // real startup order by ensuring today's missing tables before the migrator inspects v0.
        await ExecuteAsync(connection, """
            CREATE TABLE app_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE agents (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, address TEXT NOT NULL,
                direct_url TEXT NULL, invite_code TEXT NULL, info_json TEXT NULL,
                info_updated_at TEXT NULL, sort_order INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY, agent_id TEXT NOT NULL, title TEXT NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE preferences (
                id INTEGER PRIMARY KEY CHECK (id = 1), theme TEXT NOT NULL,
                sidebar_visible INTEGER NOT NULL, message_font_size TEXT NOT NULL,
                shortcut_overrides_json TEXT NOT NULL);
            CREATE TABLE conversations (
                storage_key TEXT PRIMARY KEY, address TEXT NOT NULL,
                session_id TEXT NOT NULL, envelope_json TEXT NOT NULL,
                updated_at TEXT NOT NULL);
            CREATE TABLE identity_keys (
                id INTEGER PRIMARY KEY CHECK (id = 1), address TEXT NOT NULL,
                private_seed TEXT NOT NULL);

            INSERT INTO agents (id, name, address) VALUES ('legacy-agent', 'Legacy', '0xlegacy');
            INSERT INTO sessions (id, agent_id, title, created_at, updated_at)
            VALUES ('legacy-session', 'legacy-agent', 'Old chat', '2026-07-01', '2026-07-01');
            """);

        const string envelope = """
            {
              "version": 0,
              "state": {
                "messages": [
                  { "id": 1, "role": "User", "content": "preserve this prompt", "eventStatus": "Done" },
                  { "id": 2, "role": "Agent", "content": "preserve this answer", "agentName": "Legacy", "eventStatus": "Done" }
                ],
                "createdAt": 1782864000000,
                "updatedAt": 1782864001000
              }
            }
            """;
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO conversations (storage_key, address, session_id, envelope_json, updated_at)
                VALUES ('co:agent:0xlegacy:session:legacy-session', '0xlegacy',
                        'legacy-session', $envelope, '2026-07-01');
                """;
            insert.Parameters.AddWithValue("$envelope", envelope);
            await insert.ExecuteNonQueryAsync();
        }

        await AppDatabase.CreateSchemaAsync(connection);
        await SchemaMigrator.ApplyAsync(connection);

        Assert.Equal(SchemaMigrator.LatestVersion, (int)await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name = 'remote_session_id';"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name = 'last_processed_event_id';"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('preferences') WHERE name = 'microphone_device_id';"));
        Assert.Equal(2, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM messages WHERE conversation_id = 'legacy-session';"));
        Assert.Equal("preserve this prompt", await ScalarStringAsync(connection, "SELECT content FROM messages WHERE conversation_id = 'legacy-session' AND id = 1;"));
        Assert.Equal("preserve this answer", await ScalarStringAsync(connection, "SELECT content FROM messages WHERE conversation_id = 'legacy-session' AND id = 2;"));
        Assert.Equal(2, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM message_search WHERE conversation_id = 'legacy-session';"));
        Assert.Equal("1", await ScalarStringAsync(connection, "SELECT value FROM app_meta WHERE key = 'legacy_conversations_migrated';"));
        // Keep the source blob as a recovery copy; the importer is idempotent through message PKs.
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM conversations;"));
    }

    [Fact]
    public async Task ApplyAsync_VersionFourDatabase_AddsMnemonicColumnAndKeepsTheExistingIdentity()
    {
        await using var initialized = await AppDatabase.OpenAsync();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await AppDatabase.CreateSchemaAsync(connection);
        await ExecuteAsync(connection, """
            PRAGMA user_version = 4;

            INSERT INTO identity_keys (id, address, private_seed)
            VALUES (1, '0xexisting', 'protected-seed-blob');
            """);

        await SchemaMigrator.ApplyAsync(connection);

        Assert.Equal(SchemaMigrator.LatestVersion, (int)await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('identity_keys') WHERE name = 'mnemonic';"));
        // The upgrade must not disturb an identity that already exists — its address is the one
        // every agent has authorized, and nothing can derive a phrase for its seed after the fact.
        Assert.Equal("0xexisting", await ScalarStringAsync(connection, "SELECT address FROM identity_keys WHERE id = 1;"));
        Assert.Equal("protected-seed-blob", await ScalarStringAsync(connection, "SELECT private_seed FROM identity_keys WHERE id = 1;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM identity_keys WHERE mnemonic IS NULL;"));
    }

    /// <summary>
    /// v12 rebuilds <c>agents</c> to drop <c>invite_code</c>. A rebuild is the whole-table kind of
    /// migration this repository treats as its highest risk, so this asserts the three things it
    /// could plausibly get wrong: losing rows, losing a column the rebuild has to carry forward
    /// by hand, and losing the <c>agents_delete_guard</c> trigger that <c>DROP TABLE</c> destroys.
    ///
    /// <para>The guard matters more than it looks. Nothing holds a foreign key to <c>agents</c> —
    /// v4 chose triggers instead — so if the rebuild dropped it silently, deleting an agent would
    /// simply start succeeding and leave exactly the orphaned sessions v4 exists to repair.</para>
    /// </summary>
    [Fact]
    public async Task ApplyAsync_VersionElevenDatabase_DropsInviteCodeAndKeepsAgentsAndTheirGuard()
    {
        await using var initialized = await AppDatabase.OpenAsync();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await AppDatabase.CreateSchemaAsync(connection);

        // The v11 shape, replayed: the columns v3/v6/v7/v11 added, plus all three v4 guards. The
        // two on `sessions` are not incidental detail — their bodies name `agents`, which is what
        // makes the rebuild's ALTER TABLE RENAME fail if the migration leaves them in place.
        await ExecuteAsync(connection, """
            ALTER TABLE sessions ADD COLUMN mode TEXT NOT NULL DEFAULT 'safe';
            ALTER TABLE agents ADD COLUMN icon_path TEXT NULL;
            ALTER TABLE sessions ADD COLUMN has_custom_title INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE sessions ADD COLUMN unread_count INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE sessions ADD COLUMN requires_attention INTEGER NOT NULL DEFAULT 0;

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

            PRAGMA user_version = 11;

            -- A populated invite_code is the case worth migrating: it is what a database written by
            -- a build that did wire the column up would hold, and dropping the column is the only
            -- thing that actually removes that plaintext credential from the file.
            INSERT INTO agents (id, name, address, direct_url, invite_code, icon_path, info_json, sort_order)
            VALUES
                ('agent-b', 'Second', '0xbbb', 'http://b.test', 'SECRET-CODE', 'avatars/b.png', '{"skills":[]}', 1),
                ('agent-a', 'First', '0xaaa', NULL, NULL, NULL, NULL, 0);

            INSERT INTO sessions (id, agent_id, title, created_at, updated_at, sort_order, mode)
            VALUES ('session-a', 'agent-a', 'Chat', 'now', 'now', 0, 'safe');
            """);

        await SchemaMigrator.ApplyAsync(connection);

        Assert.Equal(SchemaMigrator.LatestVersion, (int)await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(0, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name = 'invite_code';"));

        // Every row survives, and every column the rebuild had to name explicitly comes with it —
        // including icon_path, which v6 appended and a copy written from the v1 column list would
        // have silently dropped.
        Assert.Equal(2, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM agents;"));
        Assert.Equal("avatars/b.png", await ScalarStringAsync(connection, "SELECT icon_path FROM agents WHERE id = 'agent-b';"));
        Assert.Equal("http://b.test", await ScalarStringAsync(connection, "SELECT direct_url FROM agents WHERE id = 'agent-b';"));
        Assert.Equal("""{"skills":[]}""", await ScalarStringAsync(connection, "SELECT info_json FROM agents WHERE id = 'agent-b';"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT sort_order FROM agents WHERE id = 'agent-b';"));
        Assert.Equal("First", await ScalarStringAsync(connection, "SELECT name FROM agents WHERE id = 'agent-a';"));

        // The primary key still constrains the rebuilt table.
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_index_list('agents') WHERE origin = 'pk';"));

        // The guard came back, and still fires.
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM agents WHERE id = 'agent-a';";
            var error = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
                () => delete.ExecuteNonQueryAsync());
            Assert.Contains("delete agent sessions", error.Message, StringComparison.Ordinal);
        }

        // An agent with no conversations is still deletable — the guard must not have become a
        // blanket refusal.
        await ExecuteAsync(connection, "DELETE FROM agents WHERE id = 'agent-b';");
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM agents;"));

        // The other half of v4's boundary: the sessions-side guards had to be dropped for the
        // rename to succeed at all, so prove they were put back rather than quietly lost.
        await using (var orphan = connection.CreateCommand())
        {
            orphan.CommandText = """
                INSERT INTO sessions (id, agent_id, title, created_at, updated_at, sort_order, mode)
                VALUES ('session-orphan', 'agent-gone', 'Orphan', 'now', 'now', 0, 'safe');
                """;
            var error = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
                () => orphan.ExecuteNonQueryAsync());
            Assert.Contains("missing agent", error.Message, StringComparison.Ordinal);
        }

        await using (var repoint = connection.CreateCommand())
        {
            repoint.CommandText = "UPDATE sessions SET agent_id = 'agent-gone' WHERE id = 'session-a';";
            var error = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
                () => repoint.ExecuteNonQueryAsync());
            Assert.Contains("missing agent", error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// v7 replaces the <c>^Conversation \d+$</c> title test with a stored flag. The backfill has to
    /// reproduce that predicate exactly, because it decides which existing conversations may still
    /// have their title overwritten by their next message — get it wrong in one direction and a
    /// user's own title is destroyed, in the other and a conversation is stuck on "Conversation 4"
    /// forever.
    /// </summary>
    [Theory]
    // Still the placeholder: the next message may claim the title.
    [InlineData("Conversation 1", false)]
    [InlineData("Conversation 12", false)]
    [InlineData("Conversation 987", false)]
    // Settled titles, which must be left alone.
    [InlineData("Conversation 1 draft", true)]
    [InlineData("Conversation one", true)]
    [InlineData("Conversation", true)]
    [InlineData("My conversation 1", true)]
    [InlineData("Ship the migration", true)]
    [InlineData("对话 1", true)]
    public async Task ApplyAsync_VersionSixDatabase_BackfillsHasCustomTitleFromTheOldTitlePattern(
        string title,
        bool expectedHasCustomTitle)
    {
        await using var initialized = await AppDatabase.OpenAsync();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await AppDatabase.CreateSchemaAsync(connection);
        // Both columns matter: `mode` is v3's and `icon_path` is v6's own, so a database stamped at
        // 6 has by definition run both. The fixture used to stamp 6 while skipping icon_path, which
        // no real database can be — v12's rebuild names that column explicitly and is what surfaced
        // the gap.
        await ExecuteAsync(connection, """
            ALTER TABLE sessions ADD COLUMN mode TEXT NOT NULL DEFAULT 'safe';
            ALTER TABLE agents ADD COLUMN icon_path TEXT NULL;
            PRAGMA user_version = 6;

            INSERT INTO agents (id, name, address, sort_order)
            VALUES ('agent-1', 'Agent', 'address', 0);
            """);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO sessions (id, agent_id, title, created_at, updated_at, sort_order, mode)
                VALUES ('session-1', 'agent-1', $title, 'now', 'now', 0, 'safe');
                """;
            insert.Parameters.AddWithValue("$title", title);
            await insert.ExecuteNonQueryAsync();
        }

        await SchemaMigrator.ApplyAsync(connection);

        Assert.Equal(SchemaMigrator.LatestVersion, (int)await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(
            expectedHasCustomTitle ? 1 : 0,
            await ScalarLongAsync(connection, "SELECT has_custom_title FROM sessions WHERE id = 'session-1';"));
        // The title text itself is never rewritten by the migration.
        Assert.Equal(title, await ScalarStringAsync(connection, "SELECT title FROM sessions WHERE id = 'session-1';"));
    }

    /// <summary>A conversation created after v7 starts as a placeholder regardless of the wording,
    /// which is what lets the placeholder be translated.</summary>
    [Fact]
    public void NewConversation_WithLocalizedPlaceholder_IsStillTreatedAsUntitled()
    {
        var session = SessionSummary.NewConversation("agent-1", [], "对话 {0}");

        Assert.Equal("对话 1", session.Title);
        Assert.False(session.HasCustomTitle);

        Assert.True(session.TryApplyTitleFromPrompt("Ship the migration"));
        Assert.Equal("Ship the migration", session.Title);
        Assert.True(session.HasCustomTitle);

        // A second message must not re-title a conversation whose title is already settled.
        Assert.False(session.TryApplyTitleFromPrompt("Something else entirely"));
        Assert.Equal("Ship the migration", session.Title);
    }

    /// <summary>
    /// The flag has to survive the write path, or a renamed conversation would silently revert to
    /// being overwritable by its next message after a restart.
    ///
    /// Rows are seeded directly and updated through <c>UpdateSessionAsync</c> rather than through
    /// <c>SaveAsync</c>: that one reconciles the *whole* index and deletes every session not in the
    /// list it is handed, which in this shared-database collection means deleting rows other tests
    /// still own.
    /// </summary>
    [Fact]
    public async Task SessionRepository_UpdateSession_RoundTripsHasCustomTitle()
    {
        await using (var connection = await AppDatabase.OpenAsync())
        {
            await ExecuteAsync(connection, """
                INSERT OR IGNORE INTO agents (id, name, address) VALUES ('agent-title', 'Agent', '0x1');
                INSERT OR REPLACE INTO sessions (id, agent_id, title, created_at, updated_at, sort_order, mode, has_custom_title)
                VALUES
                    ('session-placeholder', 'agent-title', 'Conversation 1', 'now', 'now', 0, 'safe', 0),
                    ('session-renamed', 'agent-title', 'Conversation 2', 'now', 'now', 1, 'safe', 0);
                """);
        }

        var repository = new SessionRepository();
        var loadedBefore = (await repository.LoadAsync()).Sessions;
        var renamed = loadedBefore.Single(s => s.Id == "session-renamed");
        Assert.False(renamed.HasCustomTitle);

        Assert.True(renamed.TryRename("Weekly report"));
        await repository.UpdateSessionAsync(renamed);

        var loadedAfter = (await repository.LoadAsync()).Sessions;
        Assert.False(loadedAfter.Single(s => s.Id == "session-placeholder").HasCustomTitle);

        var reloaded = loadedAfter.Single(s => s.Id == "session-renamed");
        Assert.True(reloaded.HasCustomTitle);
        Assert.Equal("Weekly report", reloaded.Title);
    }

    [Fact]
    public async Task ApplyAsync_VersionThreeDatabase_RemovesOrphanConversationGraphAndAddsGuards()
    {
        await using var initialized = await AppDatabase.OpenAsync();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await AppDatabase.CreateSchemaAsync(connection);
        await ExecuteAsync(connection, """
            ALTER TABLE sessions ADD COLUMN mode TEXT NOT NULL DEFAULT 'safe';
            PRAGMA user_version = 3;

            INSERT INTO agents (id, name, address, sort_order)
            VALUES ('agent-valid', 'Valid agent', 'address', 0);

            INSERT INTO sessions (id, agent_id, title, created_at, updated_at, sort_order, mode)
            VALUES
                ('session-valid', 'agent-valid', 'Keep me', 'now', 'now', 0, 'safe'),
                ('session-orphan', 'agent-missing', 'Remove me', 'now', 'now', 1, 'safe');

            INSERT INTO messages (id, conversation_id, role, content, created_at)
            VALUES (1, 'session-orphan', 'user', 'orphan message', 1);

            INSERT INTO message_attachments (
                id, conversation_id, message_id, kind, file_name, size_bytes, status, created_at)
            VALUES ('attachment-orphan', 'session-orphan', 1, 'file', 'test.txt', 1, 'sent', 1);

            INSERT INTO executions (id, conversation_id, prompt, status, created_at)
            VALUES ('execution-orphan', 'session-orphan', 'test', 'complete', 1);

            INSERT INTO trace_events (id, conversation_id, execution_id, type, payload_json)
            VALUES ('trace-orphan', 'session-orphan', 'execution-orphan', 'tool_call', '{}');

            INSERT INTO app_meta (key, value)
            VALUES ('active_session_id', 'session-orphan');
            """);

        await SchemaMigrator.ApplyAsync(connection);

        Assert.Equal(SchemaMigrator.LatestVersion, (int)await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM sessions;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM sessions WHERE id = 'session-valid';"));
        Assert.Equal(0, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM messages WHERE conversation_id = 'session-orphan';"));
        Assert.Equal(0, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM message_attachments WHERE conversation_id = 'session-orphan';"));
        Assert.Equal(0, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM executions WHERE conversation_id = 'session-orphan';"));
        Assert.Equal(0, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM trace_events WHERE conversation_id = 'session-orphan';"));
        Assert.Equal(0, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM app_meta WHERE key = 'active_session_id';"));

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(connection, """
            INSERT INTO sessions (id, agent_id, title, created_at, updated_at, sort_order, mode)
            VALUES ('blocked-session', 'agent-missing', 'Blocked', 'now', 'now', 2, 'safe');
            """));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => ExecuteAsync(connection, "DELETE FROM agents WHERE id = 'agent-valid';"));
    }

    [Fact]
    public async Task ApplyAsync_NewerDatabaseVersion_ThrowsWithoutChangingVersion()
    {
        await using var initialized = await AppDatabase.OpenAsync();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var stamp = connection.CreateCommand())
        {
            stamp.CommandText = "PRAGMA user_version = 999;";
            await stamp.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrator.ApplyAsync(connection));

        Assert.Contains("newer version", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(999, await ScalarLongAsync(connection, "PRAGMA user_version;"));
    }

    private static async Task<long> ScalarLongAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async Task ExecuteAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
