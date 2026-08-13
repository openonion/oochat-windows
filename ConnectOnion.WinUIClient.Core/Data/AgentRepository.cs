using System.Text.Json.Serialization;
using ConnectOnion.WinUIClient.Models;
using Microsoft.Data.Sqlite;

namespace ConnectOnion.WinUIClient.Data;

/// <summary>
/// Persisted shape of saved agents: the agent list plus the selected id. SQLite
/// stores the list as rows and the selected pointer in app metadata.
///
/// Load/mutate/Save as a whole document — callers add or remove from
/// <see cref="Agents"/> and hand the object back. That is affordable because the list is
/// small and user-sized; the conversation tables, which are not, use incremental writes
/// instead (see <c>ConversationRepository</c>).
/// </summary>
public sealed class AgentsState
{
    [JsonPropertyName("agents")]
    public List<AgentConfig> Agents { get; set; } = new();

    [JsonPropertyName("selectedAgentId")]
    public string? SelectedAgentId { get; set; }
}

/// <summary>
/// An agent as the shell surfaces need it: enough to name it, draw it and reach it, and nothing
/// else. Notably no <c>InfoJson</c> — see <see cref="AgentRepository.LoadSummariesAsync"/> for why
/// this is a separate type rather than an <see cref="AgentConfig"/> with the blob left null.
/// </summary>
public sealed record AgentSummary(
    string Id,
    string Name,
    string Address,
    string? DirectUrl,
    string? IconPath);

/// <summary>The thin counterpart to <see cref="AgentsState"/>. Immutable, and with no route into
/// <see cref="AgentRepository.SaveAsync"/> — a partial agent record must not be writable back.</summary>
public sealed record AgentSummaryState(IReadOnlyList<AgentSummary> Agents, string? SelectedAgentId);

/// <summary>
/// Local SQLite persistence for saved agents. Port of <c>agentStorage.ts</c>.
/// </summary>
public sealed class AgentRepository
{
    private const string SelectedAgentMetaKey = "selected_agent_id";

    /// <summary>Adds one agent at the end of the user's ordering without reconciling or deleting
    /// any existing rows. The insert and optional selection change commit together.</summary>
    public async Task<bool> AppendAgentAsync(
        AgentConfig agent,
        bool makeSelected = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (string.IsNullOrWhiteSpace(agent.Id)
            || !AgentConfig.IsValidName(agent.Name)
            || !HasConnectionTarget(agent)
            || agent.Id == "local-agent")
        {
            return false;
        }

        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        // invite_code is deliberately absent — see the column's note in AppDatabase.CreateSchemaAsync.
        insert.CommandText = """
            INSERT OR IGNORE INTO agents (
                id, name, address, direct_url, icon_path,
                info_json, info_updated_at, sort_order)
            SELECT
                $id, $name, $address, $direct_url, $icon_path,
                $info_json, $info_updated_at,
                COALESCE((SELECT MAX(sort_order) + 1 FROM agents), 0);
            """;
        AppDatabase.Add(insert, "$id", agent.Id);
        AppDatabase.Add(insert, "$name", agent.Name.Trim());
        AppDatabase.Add(insert, "$address", agent.Address);
        AppDatabase.Add(insert, "$direct_url", agent.DirectUrl);
        AppDatabase.Add(insert, "$icon_path",
            string.IsNullOrWhiteSpace(agent.IconPath) ? null : agent.IconPath);
        AppDatabase.Add(insert, "$info_json", agent.InfoJson);
        AppDatabase.Add(insert, "$info_updated_at", agent.InfoUpdatedAt);

        if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            return false;

        if (makeSelected)
        {
            await AppDatabase.SetMetaAsync(connection, transaction, SelectedAgentMetaKey, agent.Id)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        StorageRevision.Bump();
        return true;
    }

    /// <summary>Changes only one agent's custom icon reference.</summary>
    public async Task<bool> UpdateIconPathAsync(
        string agentId,
        string? iconPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return false;
        var normalized = string.IsNullOrWhiteSpace(iconPath) ? null : iconPath;

        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agents
            SET icon_path = $icon_path
            WHERE id = $id AND icon_path IS NOT $icon_path;
            """;
        AppDatabase.Add(command, "$id", agentId);
        AppDatabase.Add(command, "$icon_path", normalized);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            return false;

        StorageRevision.Bump();
        return true;
    }

    /// <summary>Deletes an agent, all of its local conversation graphs, and their metadata
    /// references in one transaction. Usage history is intentionally retained.</summary>
    public async Task<bool> DeleteAgentAsync(
        string agentId,
        string? preferredSelectedAgentId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return false;

        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM agents WHERE id = $id LIMIT 1;";
            AppDatabase.Add(exists, "$id", agentId);
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                return false;
        }

        var removedSessionIds = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM sessions WHERE agent_id = $id;";
            AppDatabase.Add(select, "$id", agentId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                removedSessionIds.Add(reader.GetString(0));
        }

        await using (var deleteGraph = connection.CreateCommand())
        {
            deleteGraph.Transaction = transaction;
            deleteGraph.CommandText = """
                DELETE FROM message_attachments
                WHERE conversation_id IN (SELECT id FROM sessions WHERE agent_id = $id);
                DELETE FROM messages
                WHERE conversation_id IN (SELECT id FROM sessions WHERE agent_id = $id);
                DELETE FROM trace_events
                WHERE conversation_id IN (SELECT id FROM sessions WHERE agent_id = $id);
                DELETE FROM executions
                WHERE conversation_id IN (SELECT id FROM sessions WHERE agent_id = $id);
                DELETE FROM sessions WHERE agent_id = $id;
                DELETE FROM agents WHERE id = $id;
                """;
            AppDatabase.Add(deleteGraph, "$id", agentId);
            await deleteGraph.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await SessionRepository.PruneDeletedSessionReferencesAsync(
                connection, transaction, removedSessionIds)
            .ConfigureAwait(false);

        var selected = await AppDatabase.GetMetaAsync(connection, transaction, SelectedAgentMetaKey)
            .ConfigureAwait(false);
        if (string.Equals(selected, agentId, StringComparison.Ordinal))
        {
            var replacement = await ResolveSelectionReplacementAsync(
                    connection, transaction, preferredSelectedAgentId, cancellationToken)
                .ConfigureAwait(false);
            if (replacement is null)
            {
                await AppDatabase.DeleteMetaAsync(connection, transaction, SelectedAgentMetaKey)
                    .ConfigureAwait(false);
            }
            else
            {
                await AppDatabase.SetMetaAsync(connection, transaction, SelectedAgentMetaKey, replacement)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        StorageRevision.Bump();
        return true;
    }

    private static async Task<string?> ResolveSelectionReplacementAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? preferredAgentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id
            FROM agents
            ORDER BY CASE WHEN id = $preferred THEN 0 ELSE 1 END,
                     sort_order, name COLLATE NOCASE, id
            LIMIT 1;
            """;
        AppDatabase.Add(command, "$preferred",
            string.IsNullOrWhiteSpace(preferredAgentId) ? null : preferredAgentId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    /// <summary>Updates only the selected-agent pointer without reconciling every agent row.</summary>
    public async Task SetSelectedAgentAsync(
        string? agentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(agentId))
            await AppDatabase.DeleteMetaAsync(connection, null, SelectedAgentMetaKey).ConfigureAwait(false);
        else
            await AppDatabase.SetMetaAsync(connection, null, SelectedAgentMetaKey, agentId).ConfigureAwait(false);
        StorageRevision.Bump();
    }

    /// <summary>
    /// Updates only the cached <c>/info</c> payload for one agent.
    ///
    /// <para>The network fetch can finish long after it started. Re-saving an
    /// <see cref="AgentsState"/> snapshot here would therefore be unsafe: a selection, rename, or
    /// reorder made while the request was in flight could be overwritten by that stale snapshot.
    /// A targeted update also means a fetch that finishes after deletion cannot recreate the
    /// deleted row.</para>
    /// </summary>
    public async Task UpdateInfoAsync(
        string agentId,
        string infoJson,
        string infoUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agents
            SET info_json = $info_json,
                info_updated_at = $info_updated_at
            WHERE id = $id;
            """;
        AppDatabase.Add(command, "$id", agentId);
        AppDatabase.Add(command, "$info_json", infoJson);
        AppDatabase.Add(command, "$info_updated_at", infoUpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // The sidebar renders identity, endpoint, icon, order, and selection, but not cached
        // capability metadata. Do not invalidate and rebuild it for an invisible cache write.
    }


    /// <summary>Updates one agent's local display name without round-tripping the whole agent
    /// list. A rename dialog can remain open while another surface changes selection, order, or
    /// cached metadata, so saving its earlier <see cref="AgentsState"/> snapshot would be unsafe.</summary>
    public async Task<bool> UpdateNameAsync(
        string agentId,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId) || !AgentConfig.IsValidName(name)) return false;

        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agents
            SET name = $name
            WHERE id = $id AND name <> $name;
            """;
        AppDatabase.Add(command, "$id", agentId);
        AppDatabase.Add(command, "$name", name.Trim());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            return false;

        StorageRevision.Bump();
        return true;
    }

    /// <summary>
    /// The agent list without <c>info_json</c>, for the surfaces that only need to name, draw and
    /// reach an agent.
    ///
    /// <para><c>info_json</c> is the agent's whole cached <c>/info</c> response — skills and their
    /// descriptions included, a couple of KB each — and <see cref="LoadAsync"/> selects it every
    /// time, at thirteen call sites, of which four ever read it. The sidebar refresh, the tray menu
    /// and the search catalog are pulling that payload per agent and discarding it.</para>
    ///
    /// <para><b>Returns <see cref="AgentSummary"/> rather than a blob-less <see cref="AgentConfig"/>
    /// on purpose.</b> <see cref="SaveAsync"/> writes <c>info_json</c> from the object handed to
    /// it, so an <see cref="AgentConfig"/> read without the blob and then saved would erase every
    /// agent's cached metadata. Five production call sites round-trip a <see cref="LoadAsync"/>
    /// result straight back into <see cref="SaveAsync"/>, so this is not hypothetical — the
    /// separate type is what makes the mistake unrepresentable rather than merely discouraged.
    /// Same reasoning as <c>SessionPage</c> and the whole-index save it cannot reach.</para>
    /// </summary>
    public async Task<AgentSummaryState> LoadSummariesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        var selectedAgentId = await AppDatabase.GetMetaAsync(connection, SelectedAgentMetaKey)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // Same ordering as LoadAsync: sort_order is the user's arrangement, name and id are
        // tie-breakers so the list is stable rather than in whatever order SQLite returns rows.
        command.CommandText = """
            SELECT id, name, address, direct_url, icon_path
            FROM agents
            ORDER BY sort_order, name COLLATE NOCASE, id;
            """;

        var agents = new List<AgentSummary>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                agents.Add(new AgentSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    ReadNullableString(reader, 3),
                    ReadNullableString(reader, 4)));
            }
        }

        // The same defensive filter LoadAsync applies, for the same reason: a row with no name or
        // no endpoint renders as an unusable blank the user cannot even select to delete.
        agents = agents
            .Where(a => !string.IsNullOrWhiteSpace(a.Name)
                        && a.Id != "local-agent"
                        && (!string.IsNullOrWhiteSpace(a.Address) || !string.IsNullOrWhiteSpace(a.DirectUrl)))
            .ToList();

        if (selectedAgentId is not null && agents.All(a => a.Id != selectedAgentId))
            selectedAgentId = null;

        return new AgentSummaryState(agents, selectedAgentId);
    }

    /// <summary>
    /// Loads the agent list and selected id. Invalid rows are dropped (name
    /// required, the reserved "local-agent" id excluded, address required) and a
    /// stale selected pointer left after a deletion is cleared — mirroring the
    /// web app's defensive read.
    ///
    /// <para>Prefer <see cref="LoadSummariesAsync"/> unless you actually read <c>InfoJson</c>, or
    /// intend to write the result back through <see cref="SaveAsync"/>.</para>
    /// </summary>
    public async Task<AgentsState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        var state = new AgentsState
        {
            SelectedAgentId = await AppDatabase.GetMetaAsync(connection, SelectedAgentMetaKey).ConfigureAwait(false),
        };

        await using (var command = connection.CreateCommand())
        {
            // sort_order is the user's arrangement; name and id are tie-breakers so the list
            // is *stable* rather than in whatever order SQLite returns rows. Without them,
            // agents sharing a sort_order (everything, before the first reorder — they all
            // default to 0) could shuffle between launches. COLLATE NOCASE so the fallback
            // ordering is alphabetical the way a human reads it.
            command.CommandText = """
                SELECT id, name, address, direct_url, icon_path, info_json, info_updated_at
                FROM agents
                ORDER BY sort_order, name COLLATE NOCASE, id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                state.Agents.Add(new AgentConfig
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Address = reader.GetString(2),
                    DirectUrl = ReadNullableString(reader, 3),
                    IconPath = ReadNullableString(reader, 4),
                    InfoJson = ReadNullableString(reader, 5),
                    InfoUpdatedAt = ReadNullableString(reader, 6),
                });
            }
        }

        // Filtered on read as well as on write. The write-side check below should make this
        // redundant, but a database can predate that check or have been edited by hand, and
        // an agent with no name or no endpoint renders as an unusable blank row that the user
        // cannot even select to delete. Dropping it from the read is the recoverable outcome.
        // "local-agent" is a reserved id from the web client that never applies here.
        state.Agents = state.Agents
            .Where(a => !string.IsNullOrWhiteSpace(a.Name)
                        && a.Id != "local-agent"
                        && HasConnectionTarget(a))
            .ToList();

        // The pointer lives in app_meta with no FK to enforce it, so deleting an agent can
        // leave it dangling. Clearing it here means every caller sees "nothing selected"
        // rather than a selected id that matches no row.
        if (state.SelectedAgentId is not null &&
            state.Agents.All(a => a.Id != state.SelectedAgentId))
        {
            state.SelectedAgentId = null;
        }

        return state;
    }

    /// <summary>Loads only the selected agent, including its capability blob. Chat and detail
    /// navigation need that one record; reading and sorting the entire agent table made every
    /// page switch scale with unrelated agents.</summary>
    public async Task<AgentConfig?> GetSelectedAgentAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        var selectedId = await AppDatabase.GetMetaAsync(connection, SelectedAgentMetaKey).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(selectedId)
            ? null
            : await GetAgentAsync(connection, selectedId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads one full agent record by id.</summary>
    public async Task<AgentConfig?> GetAgentAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetAgentAsync(connection, agentId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AgentConfig?> GetAgentAsync(
        SqliteConnection connection,
        string agentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, address, direct_url, icon_path, info_json, info_updated_at
            FROM agents
            WHERE id = $id
            LIMIT 1;
            """;
        AppDatabase.Add(command, "$id", agentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

        var agent = new AgentConfig
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Address = reader.GetString(2),
            DirectUrl = ReadNullableString(reader, 3),
            IconPath = ReadNullableString(reader, 4),
            InfoJson = ReadNullableString(reader, 5),
            InfoUpdatedAt = ReadNullableString(reader, 6),
        };
        return !string.IsNullOrWhiteSpace(agent.Name)
               && agent.Id != "local-agent"
               && HasConnectionTarget(agent)
            ? agent
            : null;
    }

    /// <summary>
    /// Reconciles the whole agent list for test seeding. Existing rows are upserted in the caller's order, then
    /// omitted agents are removed after their complete conversation graph has been deleted.
    /// This ordering keeps <c>sessions.agent_id</c> valid throughout the transaction and makes
    /// repository-level deletion safe. Production code uses targeted writes so a stale snapshot
    /// cannot overwrite unrelated agent changes.
    /// </summary>
    public async Task SaveAsync(AgentsState state, CancellationToken cancellationToken = default)
    {
        await using var connection = await AppDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        // One transaction over reconciliation, graph cleanup, and the selected-id write: a
        // crash mid-save must not be able to leave a partially updated agent/session graph.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var validAgents = state.Agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Id)
                            && !string.IsNullOrWhiteSpace(agent.Name)
                            && HasConnectionTarget(agent)
                            && agent.Id != "local-agent")
            .ToList();

        for (var index = 0; index < validAgents.Count; index++)
        {
            var agent = validAgents[index];

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            // invite_code is deliberately absent — see the column's note in AppDatabase.CreateSchemaAsync.
            insert.CommandText = """
                INSERT INTO agents (id, name, address, direct_url, icon_path, info_json, info_updated_at, sort_order)
                VALUES ($id, $name, $address, $direct_url, $icon_path, $info_json, $info_updated_at, $sort_order)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    address = excluded.address,
                    direct_url = excluded.direct_url,
                    icon_path = excluded.icon_path,
                    info_json = excluded.info_json,
                    info_updated_at = excluded.info_updated_at,
                    sort_order = excluded.sort_order;
                """;
            AppDatabase.Add(insert, "$id", agent.Id);
            AppDatabase.Add(insert, "$name", agent.Name);
            AppDatabase.Add(insert, "$address", agent.Address);
            AppDatabase.Add(insert, "$direct_url", agent.DirectUrl);
            // Blank and null both mean "no custom icon"; store the one representation so a
            // caller that cleared the path can never leave an empty string the UI would resolve.
            AppDatabase.Add(insert, "$icon_path",
                string.IsNullOrWhiteSpace(agent.IconPath) ? null : agent.IconPath);
            AppDatabase.Add(insert, "$info_json", agent.InfoJson);
            AppDatabase.Add(insert, "$info_updated_at", agent.InfoUpdatedAt);
            // Position in the caller's list *is* the persisted order — reordering the sidebar
            // is just saving the list in its new order, with no explicit reorder API.
            AppDatabase.Add(insert, "$sort_order", index);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await DeleteRemovedAgentGraphsAsync(
                connection,
                transaction,
                validAgents.Select(agent => agent.Id).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

        // Delete rather than store an empty string, so "no selection" has exactly one
        // representation (a missing key) for GetMetaAsync's null to mean.
        if (string.IsNullOrWhiteSpace(state.SelectedAgentId))
        {
            await AppDatabase.DeleteMetaAsync(connection, transaction, SelectedAgentMetaKey)
                .ConfigureAwait(false);
        }
        else
        {
            await AppDatabase.SetMetaAsync(connection, transaction, SelectedAgentMetaKey, state.SelectedAgentId!)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        StorageRevision.Bump();
    }

    private static async Task DeleteRemovedAgentGraphsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> keepAgentIds,
        CancellationToken cancellationToken)
    {
        var sessionPredicate = keepAgentIds.Count == 0
            ? "1 = 1"
            : $"agent_id NOT IN ({string.Join(", ", keepAgentIds.Select((_, index) => $"$keep{index}"))})";
        var agentPredicate = keepAgentIds.Count == 0
            ? "1 = 1"
            : $"id NOT IN ({string.Join(", ", keepAgentIds.Select((_, index) => $"$keep{index}"))})";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            DELETE FROM message_attachments
            WHERE conversation_id IN (SELECT id FROM sessions WHERE {sessionPredicate});

            DELETE FROM messages
            WHERE conversation_id IN (SELECT id FROM sessions WHERE {sessionPredicate});

            DELETE FROM trace_events
            WHERE conversation_id IN (SELECT id FROM sessions WHERE {sessionPredicate});

            DELETE FROM executions
            WHERE conversation_id IN (SELECT id FROM sessions WHERE {sessionPredicate});

            DELETE FROM sessions WHERE {sessionPredicate};
            DELETE FROM agents WHERE {agentPredicate};
            """;
        for (var index = 0; index < keepAgentIds.Count; index++)
        {
            AppDatabase.Add(command, $"$keep{index}", keepAgentIds[index]);
        }
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>An agent is only usable if it can be reached somehow — by relay address or by
    /// direct URL. Either alone is enough; neither makes the row dead weight, which is why
    /// this gates both the read and the write.</summary>
    private static bool HasConnectionTarget(AgentConfig agent)
        => !string.IsNullOrWhiteSpace(agent.Address) ||
           !string.IsNullOrWhiteSpace(agent.DirectUrl);
}
