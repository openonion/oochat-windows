using Microsoft.Data.Sqlite;

namespace ConnectOnion.WinUIClient.Data;

/// <summary>
/// SQLite backing store for local app state, and the only place a connection is opened.
///
/// Everything is relational — <c>messages</c> is one row per rendered chat bubble, not a
/// serialized conversation envelope, which is what lets a turn be written incrementally
/// (O(turn) rather than O(conversation)). The only JSON in the schema is genuinely opaque
/// payload: an agent's <c>/info</c> response, a trace event's body, the preference blobs.
/// </summary>
public static class AppDatabase
{
    // First open wins and everyone else waits: schema creation plus migration must happen
    // exactly once, and repositories open connections concurrently from the very first frame.
    private static readonly SemaphoreSlim InitGate = new(1, 1);
    private static bool _initialized;

    public static string DatabasePath => Path.Combine(AppStorage.RootDir, "connectonion.db");

    /// <summary>Opens a ready-to-use connection, initializing the database on first call.
    /// Callers own the returned connection and must dispose it.</summary>
    public static async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        // foreign_keys is per-connection in SQLite, not a property of the file, so it has to be
        // asserted on every open rather than once at init — without it the FK declarations in the
        // schema are inert and the ordered deletes in ConversationRepository would silently leave
        // orphans instead of failing loudly. It rides in the connection string (see
        // ConnectionString) rather than as a `PRAGMA foreign_keys = ON;` statement issued here:
        // repositories open a connection per call, so that statement was an extra prepare and
        // round trip on every single database operation — four of them for one sidebar refresh
        // before it has read anything.
        var connection = new SqliteConnection(ConnectionString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // WAL is persistent (it lives in the file header) but `synchronous` is not — it is a
        // property of the connection and resets to FULL on every open, so it belongs here rather
        // than beside the journal_mode line in EnsureInitializedAsync. FULL means an fsync on
        // *every* commit, and nearly every write in this app is its own transaction (a turn's
        // messages, a session rename, a preference, a pin), so that fsync is paid per user action.
        //
        // NORMAL is the documented companion to WAL: a committed transaction still survives a
        // process crash, because the WAL file is already written — only an OS crash or power loss
        // can cost the last few. For a local cache of conversations that is the right trade.
        // Measured at 200 separate commits: FULL 53 ms, NORMAL 4 ms.
        //
        // This is the connection's one setup statement. foreign_keys used to be a second one and
        // now rides in the connection string instead, so the per-open cost is unchanged.
        await ExecuteAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        // Fast path: an unsynchronized read of a bool that only ever goes false→true. A torn
        // read is not possible, and the worst case is redundantly taking the gate below.
        if (_initialized) return;

        await InitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check: several callers can be queued on the gate, and only the first
            // should do the work.
            if (_initialized) return;

            // winsqlite3 is the copy of SQLite that ships with Windows, so the app carries no
            // native SQLite binary of its own. This must run before any connection is opened.
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
            AppStorage.EnsureDirectories();
            await using var connection = new SqliteConnection(ConnectionString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // WAL is persistent (it lives in the file header), so unlike foreign_keys this one
            // genuinely is a once-per-database setting. It matters here because a turn writes
            // while the UI reads, and WAL lets those not block each other.
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);

            // Baseline first (creates anything missing), then step any older database forward.
            // CreateSchemaAsync alone cannot evolve a table that already exists — that is what
            // SchemaMigrator is for. See its doc comment before changing any table above.
            await CreateSchemaAsync(connection).ConfigureAwait(false);
            await SchemaMigrator.ApplyAsync(connection).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            InitGate.Release();
        }
    }

    private static string ConnectionString()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            // ReadWriteCreate: first run has no file, and creating it here is what makes
            // "delete the .db to reset dev state" work without any other setup.
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Applied by the provider as part of opening the connection, which is why OpenAsync
            // no longer issues a PRAGMA of its own. Same guarantee, one fewer round trip per open.
            ForeignKeys = true,
            // Deliberately *not* Cache=Shared. Shared-cache mode makes every connection in the
            // process share one page cache and take table-level locks against each other, which
            // is the opposite of what the WAL setting below is here for: a turn writing while the
            // UI reads is exactly the case WAL keeps unblocked, and shared cache reintroduces the
            // contention (as SQLITE_LOCKED rather than SQLITE_BUSY, which retry logic handles
            // less well). The two settings were working against each other. Connection pooling is
            // unaffected — it is on by default and is what keeps per-call opens cheap.
        };
        return builder.ToString();
    }

    /// <summary>
    /// The version-1 baseline: every table as a pre-versioning build left it. Internal rather than
    /// private so tests can build a genuine version-0 database to migrate — <see cref="SchemaMigrator"/>
    /// only ever runs against a database that already has this shape (see <see cref="EnsureInitializedAsync"/>),
    /// and a migration that alters an existing table has nothing to alter without it.
    /// </summary>
    internal static async Task CreateSchemaAsync(SqliteConnection connection)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS app_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            -- This is the frozen v1 baseline shape, not the current one. It still declares
            -- invite_code (which v12 removes) and still lacks icon_path (which v6 adds), for the
            -- same reason: migrations evolve *from* this, so editing it would change what an old
            -- database is upgraded from and desynchronise every recorded user_version.
            -- A fresh database is created here and then walked through migrations 2..N like any
            -- other, which is also what keeps those migrations exercised on every clean install.
            CREATE TABLE IF NOT EXISTS agents (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                address TEXT NOT NULL,
                direct_url TEXT NULL,
                invite_code TEXT NULL,
                info_json TEXT NULL,
                info_updated_at TEXT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL,
                title TEXT NOT NULL,
                remote_session_id TEXT NULL,
                last_processed_event_id TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_sessions_agent_updated
                ON sessions(agent_id, updated_at DESC);

            CREATE TABLE IF NOT EXISTS preferences (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                theme TEXT NOT NULL,
                sidebar_visible INTEGER NOT NULL,
                message_font_size TEXT NOT NULL,
                shortcut_overrides_json TEXT NOT NULL,
                microphone_device_id TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER NOT NULL,
                conversation_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT,
                agent_name TEXT,
                event_kind TEXT,
                event_key TEXT,
                event_eyebrow TEXT,
                event_title TEXT,
                event_detail TEXT,
                event_meta TEXT,
                event_args TEXT,
                event_result TEXT,
                event_status TEXT,
                is_onboarding INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL,
                PRIMARY KEY (conversation_id, id),
                FOREIGN KEY (conversation_id) REFERENCES sessions(id)
            );

            CREATE TABLE IF NOT EXISTS message_attachments (
                id TEXT NOT NULL,
                conversation_id TEXT NOT NULL,
                message_id INTEGER NOT NULL,
                kind TEXT NOT NULL,
                file_name TEXT NOT NULL,
                mime_type TEXT NULL,
                size_bytes INTEGER NOT NULL DEFAULT 0,
                local_cache_path TEXT NULL,
                remote_uri TEXT NULL,
                status TEXT NOT NULL DEFAULT 'sent',
                created_at INTEGER NOT NULL,
                PRIMARY KEY (conversation_id, message_id, id),
                FOREIGN KEY (conversation_id) REFERENCES sessions(id)
            );

            -- Both of these duplicated the index SQLite already builds for the composite primary
            -- key above them ((conversation_id, id) exactly; (conversation_id, message_id) as a
            -- prefix), so they served no read and cost a second b-tree write on every insert.
            -- Dropped explicitly so databases created before this change shed them too.
            DROP INDEX IF EXISTS ix_messages_conversation;
            DROP INDEX IF EXISTS ix_message_attachments_message;

            CREATE TABLE IF NOT EXISTS executions (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                remote_session_id TEXT,
                prompt TEXT NOT NULL,
                result TEXT,
                status TEXT NOT NULL,
                duration_ms REAL,
                created_at INTEGER NOT NULL,
                FOREIGN KEY (conversation_id) REFERENCES sessions(id)
            );

            CREATE INDEX IF NOT EXISTS ix_executions_conversation
                ON executions(conversation_id, created_at DESC);

            CREATE TABLE IF NOT EXISTS trace_events (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                execution_id TEXT,
                session_id TEXT,
                type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                ts REAL,
                FOREIGN KEY (conversation_id) REFERENCES sessions(id)
            );

            CREATE INDEX IF NOT EXISTS ix_trace_events_conversation
                ON trace_events(conversation_id, ts);

            CREATE TABLE IF NOT EXISTS identity_keys (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                address TEXT NOT NULL,
                private_seed TEXT NOT NULL
            );
            """).ConfigureAwait(false);
    }

    // app_meta is the generic key/value scratch table for small singleton values that don't
    // deserve a column. Note the schema *version* is deliberately not one of them — it lives
    // in SQLite's own PRAGMA user_version so it can be written inside the same transaction as
    // the migration that earned it (see SchemaMigrator).

    public static async Task<string?> GetMetaAsync(SqliteConnection connection, string key)
        => await GetMetaAsync(connection, null, key).ConfigureAwait(false);

    /// <summary>Reads metadata on the caller's transaction when several related rows must move
    /// atomically. The two-argument overload remains the ordinary read path.</summary>
    public static async Task<string?> GetMetaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string key)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM app_meta WHERE key = $key;";
        Add(command, "$key", key);
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value as string;
    }

    public static async Task SetMetaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string key,
        string value)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_meta (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        Add(command, "$key", key);
        Add(command, "$value", value);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public static async Task DeleteMetaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string key)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM app_meta WHERE key = $key;";
        Add(command, "$key", key);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Binds a parameter, mapping .NET <c>null</c> to <see cref="DBNull"/>.
    /// Every repository binds through this rather than <c>AddWithValue</c> directly, because
    /// passing a bare null to AddWithValue throws instead of writing SQL NULL — and most of
    /// the columns in this schema are nullable.</summary>
    public static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
