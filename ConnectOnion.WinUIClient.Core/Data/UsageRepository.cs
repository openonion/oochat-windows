using System.Globalization;
using ConnectOnion.WinUIClient.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConnectOnion.WinUIClient.Data;

/// <summary>
/// The token-usage ledger (<c>usage_events</c>, schema v2).
///
/// <b>The ledger is independent of conversation and agent lifetime.</b> Deleting a conversation or
/// an agent does not touch it — the table has no foreign key and is not part of any cascade. That is
/// the whole point: the tokens were spent, and that stays true after the chat is gone. Totals that
/// shrank whenever a user tidied their sidebar would be unable to answer any question honestly.
///
/// The only way usage is ever removed is <see cref="ClearAsync"/>, which the user invokes
/// explicitly from the Usage panel.
///
/// Aggregation is done in SQL (<c>GROUP BY model</c>), never by reading rows into memory — the
/// ledger grows without bound, and "load everything and group in C#" would become the slowest thing
/// in Settings.
/// </summary>
public sealed class UsageRepository
{
    private static readonly Action<ILogger, string, Exception?> LogDatabaseFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "UsageDatabaseFailure"),
            "Usage database operation {Operation} failed");
    private readonly ILogger<UsageRepository> _logger;

    public UsageRepository(ILogger<UsageRepository>? logger = null)
        => _logger = logger ?? NullLogger<UsageRepository>.Instance;

    /// <summary>
    /// Appends a turn's usage. Idempotent: the primary key is the server's event id, so re-persisting
    /// the same run (a retry, a replay) updates rather than double-counts.
    /// </summary>
    public async Task InsertAsync(IReadOnlyList<UsageRecord> records)
    {
        if (records.Count == 0) return;

        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO usage_events (
                    id, conversation_id, agent_id, agent_name, model,
                    input_tokens, output_tokens, cached_tokens, cache_write_tokens,
                    duration_ms, created_at)
                VALUES ($id, $conv, $agent, $agent_name, $model,
                        $in, $out, $cached, $cache_write, $duration, $created)
                ON CONFLICT(id) DO UPDATE SET
                    input_tokens       = excluded.input_tokens,
                    output_tokens      = excluded.output_tokens,
                    cached_tokens      = excluded.cached_tokens,
                    cache_write_tokens = excluded.cache_write_tokens,
                    duration_ms        = excluded.duration_ms;
                """;
            // The UPDATE arm covers only the measured values. Identity and provenance
            // (conversation, agent, agent_name, model, created_at) are left as first written,
            // so a replay cannot re-attribute an already-recorded call to a different agent —
            // and agent_name in particular is a deliberate snapshot, kept so a deleted agent's
            // rows can still be labelled in the panel.

            var id = cmd.Parameters.Add("$id", SqliteType.Text);
            var conv = cmd.Parameters.Add("$conv", SqliteType.Text);
            var agent = cmd.Parameters.Add("$agent", SqliteType.Text);
            var agentName = cmd.Parameters.Add("$agent_name", SqliteType.Text);
            var model = cmd.Parameters.Add("$model", SqliteType.Text);
            var input = cmd.Parameters.Add("$in", SqliteType.Integer);
            var output = cmd.Parameters.Add("$out", SqliteType.Integer);
            var cached = cmd.Parameters.Add("$cached", SqliteType.Integer);
            var cacheWrite = cmd.Parameters.Add("$cache_write", SqliteType.Integer);
            var duration = cmd.Parameters.Add("$duration", SqliteType.Real);
            var created = cmd.Parameters.Add("$created", SqliteType.Integer);
            // Explicit Prepare because the loop below rebinds and re-executes this one command
            // per record; compiling the statement once is the point of holding the parameter
            // handles in locals rather than looking them up by name each iteration.
            cmd.Prepare();

            foreach (var r in records)
            {
                id.Value = r.Id;
                conv.Value = r.ConversationId ?? (object)DBNull.Value;
                agent.Value = r.AgentId ?? (object)DBNull.Value;
                agentName.Value = r.AgentName ?? (object)DBNull.Value;
                model.Value = r.Model;
                input.Value = r.InputTokens;
                output.Value = r.OutputTokens;
                cached.Value = r.CachedTokens;
                cacheWrite.Value = r.CacheWriteTokens;
                duration.Value = r.DurationMs ?? (object)DBNull.Value;
                created.Value = r.CreatedAt;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Accounting must never take a turn down with it.
            LogFailure(nameof(InsertAsync), ex);
        }
    }

    /// <summary>Per-model totals for the window, biggest spender first.</summary>
    public async Task<IReadOnlyList<ModelUsageSummary>> GetByModelAsync(long? sinceUnixMs, string? agentId = null)
    {
        var results = new List<ModelUsageSummary>();

        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            var source = sinceUnixMs is not null
                ? "usage_events INDEXED BY ix_usage_created"
                : "usage_events";
            var filters = new List<string>(2);
            if (sinceUnixMs is not null) filters.Add("created_at >= $since");
            if (agentId is not null) filters.Add("agent_id = $agent");
            var where = filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}";
            cmd.CommandText = $"""
                SELECT model,
                       COUNT(*)                       AS calls,
                       SUM(input_tokens)              AS input_tokens,
                       SUM(output_tokens)             AS output_tokens,
                       SUM(cached_tokens)             AS cached_tokens,
                       SUM(cache_write_tokens)        AS cache_write_tokens,
                       COALESCE(SUM(duration_ms), 0)  AS duration_ms
                FROM {source}
                {where}
                GROUP BY model
                ORDER BY (SUM(input_tokens) + SUM(output_tokens)) DESC;
                """;
            // The four fixed filter combinations are assembled from constant clauses so a
            // windowed query remains a real created_at range seek instead of an optional-OR scan.
            // Ranked by input+output only: cached and cache-write tokens are reported but do
            // not decide the order, since they are not what the user is being billed for.
            if (sinceUnixMs is not null) AppDatabase.Add(cmd, "$since", sinceUnixMs);
            if (agentId is not null) AppDatabase.Add(cmd, "$agent", agentId);

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                results.Add(new ModelUsageSummary(
                    Model: reader.GetString(0),
                    Calls: reader.GetInt64(1),
                    InputTokens: reader.GetInt64(2),
                    OutputTokens: reader.GetInt64(3),
                    CachedTokens: reader.GetInt64(4),
                    CacheWriteTokens: reader.GetInt64(5),
                    TotalDurationMs: reader.GetDouble(6)));
            }
        }
        catch (Exception ex)
        {
            LogFailure(nameof(GetByModelAsync), ex);
        }

        return results;
    }

    /// <summary>
    /// One row per <b>local</b> calendar day that has any usage, for the activity heatmap. Days
    /// with no usage are simply absent — the caller builds the full calendar grid and treats a
    /// missing day as zero, which is far cheaper than asking SQLite to synthesise 365 empty rows.
    /// </summary>
    /// <param name="sinceUnixMs">Inclusive lower bound, or null for the whole ledger.</param>
    /// <remarks>
    /// <para>The day bucket is computed <i>in SQL</i>, like every other aggregate here: the ledger
    /// grows without bound, and reading a year of individual calls into memory to group them in
    /// C# is exactly the mistake this class is written to avoid.</para>
    /// <para>The day must be a <i>local</i> day — <c>created_at</c> is Unix milliseconds UTC, and
    /// grouping on the raw value cuts days at UTC midnight, so a 9pm turn would land on tomorrow
    /// for anyone east of Greenwich. The obvious spelling, SQLite's <c>'localtime'</c> modifier,
    /// <b>does not work here</b>: this app runs on <c>winsqlite3</c>, the copy of SQLite that
    /// ships with Windows, which is built without local-time support — <c>date(…, 'localtime')</c>
    /// returns NULL, every bucket is discarded, and the panel silently shows nothing. The offset
    /// is therefore applied as arithmetic before bucketing, which keeps the grouping in SQL.</para>
    /// <para>Known imprecision, accepted deliberately: one offset is applied to the whole window,
    /// so across a daylight-saving boundary a call within an hour of local midnight can fall in
    /// the neighbouring square. Carrying historical offsets would need a time-zone table SQLite
    /// does not have, and being an hour out on two nights a year does not change what a heatmap
    /// says. Buckets also follow the machine's <i>current</i> zone, so travelling re-slices
    /// history rather than re-labelling it — the same trade the rest of the panel makes by
    /// reading <c>DateTimeOffset.Now</c>.</para>
    /// </remarks>
    public async Task<IReadOnlyList<DailyUsageTotal>> GetDailyTotalsAsync(long? sinceUnixMs)
    {
        var results = new List<DailyUsageTotal>();

        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            var source = sinceUnixMs is null
                ? "usage_events"
                : "usage_events INDEXED BY ix_usage_created";
            var where = sinceUnixMs is null ? "" : "WHERE created_at >= $since";
            cmd.CommandText = $"""
                SELECT date((created_at + $offset_ms) / 1000, 'unixepoch') AS day,
                       SUM(input_tokens)  AS input_tokens,
                       SUM(output_tokens) AS output_tokens,
                       COUNT(*)           AS calls
                FROM {source}
                {where}
                GROUP BY day
                ORDER BY day;
                """;
            if (sinceUnixMs is not null) AppDatabase.Add(cmd, "$since", sinceUnixMs);
            AppDatabase.Add(
                cmd, "$offset_ms", (long)DateTimeOffset.Now.Offset.TotalMilliseconds);

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                // date() always yields 'YYYY-MM-DD'; a row that somehow doesn't parse is skipped
                // rather than throwing, because one malformed bucket must not blank the panel.
                if (!DateOnly.TryParseExact(
                        reader.GetString(0), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    continue;
                }

                results.Add(new DailyUsageTotal(
                    Date: day,
                    InputTokens: reader.GetInt64(1),
                    OutputTokens: reader.GetInt64(2),
                    Calls: reader.GetInt64(3)));
            }
        }
        catch (Exception ex)
        {
            LogFailure(nameof(GetDailyTotalsAsync), ex);
        }

        return results;
    }

    /// <summary>Timestamp of the oldest recorded call, so the panel can say what "all time" means.
    /// Null when the ledger is empty.</summary>
    public async Task<DateTimeOffset?> GetFirstRecordedAsync()
    {
        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT MIN(created_at) FROM usage_events;";

            var value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            if (value is null || value is DBNull) return null;

            // Stored UTC, shown local — this is a date the user reads ("since 3 March"), so it
            // has to be in their own timezone rather than the ledger's.
            return DateTimeOffset.FromUnixTimeMilliseconds(
                Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)).ToLocalTime();
        }
        catch (Exception ex)
        {
            LogFailure(nameof(GetFirstRecordedAsync), ex);
            return null;
        }
    }

    /// <summary>
    /// Erases usage history — the only path that ever deletes from this table, and only ever on an
    /// explicit user action. <paramref name="sinceUnixMs"/> null clears everything.
    /// </summary>
    public async Task ClearAsync(long? sinceUnixMs = null)
    {
        try
        {
            await using var connection = await AppDatabase.OpenAsync().ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sinceUnixMs is null
                ? "DELETE FROM usage_events;"
                : "DELETE FROM usage_events WHERE created_at >= $since;";
            if (sinceUnixMs is not null) AppDatabase.Add(cmd, "$since", sinceUnixMs);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailure(nameof(ClearAsync), ex);
        }
    }

    private void LogFailure(string operation, Exception ex)
    {
        LogDatabaseFailure(_logger, operation, ex);
    }
}
