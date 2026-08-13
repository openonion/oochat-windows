using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.IntegrationTests.Database;

[Collection(DatabaseCollection.Name)]
public sealed class AgentRepositoryTests
{
    private readonly AgentRepository _repository = new();

    /// <summary>
    /// The thin read returns the same agents in the same order as the full one, and carries no
    /// <c>info_json</c>.
    ///
    /// <para>The type is the safety mechanism here: <c>SaveAsync</c> writes <c>info_json</c> from
    /// whatever object it is given, and five production call sites round-trip a load straight back
    /// into it — so a blob-less <c>AgentConfig</c> would erase every agent's cached metadata.
    /// <c>AgentSummary</c> has no route into <c>SaveAsync</c>, and this test would stop compiling
    /// if that ever changed, which is the point.</para>
    /// </summary>
    [Fact]
    public async Task LoadSummariesAsync_MatchesLoadAsync_WithoutCarryingInfoJson()
    {
        var agents = Enumerable.Range(0, 3).Select(i => new AgentConfig
        {
            Id = $"summary-{i}",
            Name = $"Summary Agent {i}",
            Address = $"0x{i}",
            DirectUrl = i % 2 == 0 ? $"https://agent-{i}.example.test" : null,
            IconPath = i == 0 ? "avatars/summary-0.png" : null,
            InfoJson = "{\"skills\":[{\"name\":\"a\",\"description\":\"a big cached payload\"}]}",
            InfoUpdatedAt = "2026-08-05T00:00:00Z",
        }).ToList();
        await _repository.SaveAsync(new AgentsState { Agents = agents, SelectedAgentId = "summary-1" });

        var full = await _repository.LoadAsync();
        var thin = await _repository.LoadSummariesAsync();

        Assert.Equal(full.Agents.Select(a => a.Id), thin.Agents.Select(a => a.Id));
        Assert.Equal(full.SelectedAgentId, thin.SelectedAgentId);
        Assert.Equal(
            full.Agents.Select(a => (a.Name, a.Address, a.DirectUrl, a.IconPath)),
            thin.Agents.Select(a => (a.Name, a.Address, a.DirectUrl, a.IconPath)));
        // Address + DirectUrl are exactly what a reachability probe dials, so the sidebar can
        // drive presence from a summary without ever reading a full agent record.
        Assert.All(thin.Agents, agent =>
            Assert.False(string.IsNullOrWhiteSpace(agent.Address) && string.IsNullOrWhiteSpace(agent.DirectUrl)));

        // The blob is still on disk — the thin read skips it, it does not drop it.
        Assert.All(full.Agents, agent => Assert.Contains("cached payload", agent.InfoJson!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetSelectedAgentAsync_LoadsOnlyTheSelectedFullRecord()
    {
        await _repository.SaveAsync(new AgentsState
        {
            Agents =
            [
                new AgentConfig { Id = "selected-one", Name = "Selected", Address = "0x1", InfoJson = "{\"model\":\"chosen\"}" },
                new AgentConfig { Id = "not-selected", Name = "Other", Address = "0x2", InfoJson = "{\"model\":\"other\"}" },
            ],
            SelectedAgentId = "selected-one",
        });

        var selected = await _repository.GetSelectedAgentAsync();

        Assert.NotNull(selected);
        Assert.Equal("selected-one", selected.Id);
        Assert.Contains("chosen", selected.InfoJson!, StringComparison.Ordinal);
        Assert.Equal("not-selected", (await _repository.GetAgentAsync("not-selected"))!.Id);
    }

    [Fact]
    public async Task SaveLoadAndDelete_Agent_RoundTripsLifecycleAndAllFields()
    {
        var agent = new AgentConfig
        {
            Id = "agent-crud",
            Name = "CRUD Agent",
            Address = "0x1234",
            DirectUrl = "https://agent.example.test",
            IconPath = "avatars/agent-crud.png",
            InfoJson = "{\"name\":\"remote\"}",
            InfoUpdatedAt = "2026-07-13T00:00:00Z",
        };
        await _repository.SaveAsync(new AgentsState
        {
            Agents = new List<AgentConfig> { agent },
            SelectedAgentId = agent.Id,
        });

        var loaded = await _repository.LoadAsync();
        var actual = Assert.Single(loaded.Agents);
        Assert.Equal(agent.Id, loaded.SelectedAgentId);
        Assert.Equal(agent.Name, actual.Name);
        Assert.Equal(agent.Address, actual.Address);
        Assert.Equal(agent.DirectUrl, actual.DirectUrl);
        Assert.Equal(agent.IconPath, actual.IconPath);
        Assert.Equal(agent.InfoJson, actual.InfoJson);
        Assert.Equal(agent.InfoUpdatedAt, actual.InfoUpdatedAt);

        await _repository.SaveAsync(new AgentsState());
        var deleted = await _repository.LoadAsync();
        Assert.Empty(deleted.Agents);
        Assert.Null(deleted.SelectedAgentId);
    }

    /// <summary>
    /// <c>agents.invite_code</c> was removed by schema v12 and must not come back. An invite code
    /// is a trust credential; persisting it in plaintext would put a usable secret in the same
    /// file whose other secret is DPAPI-protected specifically so that copying the database yields
    /// nothing. It belongs in memory only, on <c>AgentConnectionService.InviteCode</c>.
    ///
    /// <para>Asserted rather than left to the migration: the old plumbing round-tripped correctly
    /// and had a passing test for it, which is exactly why nothing flagged that no production code
    /// ever populated it. This fails if the column is re-added.</para>
    /// </summary>
    [Fact]
    public async Task InviteCodeColumn_IsAbsentFromTheAgentsTable()
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name = 'invite_code';";
        Assert.Equal(0L, Assert.IsType<long>(await command.ExecuteScalarAsync()));
    }

    /// <summary>
    /// The v12 rebuild drops and recreates <c>agents</c>, which destroys the BEFORE DELETE trigger
    /// v4 put there. Losing it would be silent — deletes would simply start succeeding — and the
    /// result is the orphaned-session state v4 exists to repair.
    /// </summary>
    [Fact]
    public async Task AgentDeleteGuard_SurvivesTheV12TableRebuild()
    {
        await _repository.AppendAgentAsync(
            new AgentConfig { Id = "agent-guarded", Name = "Guarded", Address = "0xguarded" },
            makeSelected: true);
        await new SessionRepository().AppendSessionAsync(
            new SessionSummary
            {
                Id = "session-guarded",
                AgentId = "agent-guarded",
                Title = "Guarded",
                CreatedAt = "2026-01-01T00:00:00Z",
                UpdatedAt = "2026-01-01T00:00:00Z",
            },
            makeActive: false);

        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agents WHERE id = 'agent-guarded';";

        var error = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Contains("delete agent sessions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_DirectUrlOnlyAgent_PersistsAsValidConnectionTarget()
    {
        var agent = new AgentConfig
        {
            Id = "direct-only",
            Name = "Direct",
            Address = "",
            DirectUrl = "wss://direct.example.test",
        };

        await _repository.SaveAsync(new AgentsState { Agents = new List<AgentConfig> { agent } });

        Assert.Equal("direct-only", Assert.Single((await _repository.LoadAsync()).Agents).Id);
    }

    [Fact]
    public async Task SaveAsync_InvalidAndReservedAgents_SkipsRowsAndStaleSelection()
    {
        await _repository.SaveAsync(new AgentsState
        {
            Agents = new List<AgentConfig>
            {
                new() { Id = "", Name = "Missing id", Address = "0x1" },
                new() { Id = "missing-name", Name = "", Address = "0x2" },
                new() { Id = "missing-target", Name = "No target" },
                new() { Id = "local-agent", Name = "Reserved", Address = "0x3" },
            },
            SelectedAgentId = "missing-target",
        });

        var loaded = await _repository.LoadAsync();
        Assert.Empty(loaded.Agents);
        Assert.Null(loaded.SelectedAgentId);
    }

    [Fact]
    public async Task SaveAsync_AgentList_PreservesExplicitOrder()
    {
        var agents = new[]
        {
            new AgentConfig { Id = "order-z", Name = "Zulu", Address = "0x1" },
            new AgentConfig { Id = "order-a", Name = "Alpha", Address = "0x2" },
        };

        await _repository.SaveAsync(new AgentsState { Agents = agents.ToList() });

        Assert.Equal(new[] { "order-z", "order-a" }, (await _repository.LoadAsync()).Agents.Select(agent => agent.Id));
    }

    [Fact]
    public async Task UpdateInfoAsync_PreservesSelectionAndOtherAgentFields()
    {
        var fetchingAgent = new AgentConfig
        {
            Id = "info-fetching",
            Name = "Original name",
            Address = "0x1",
        };
        var selectedAgent = new AgentConfig
        {
            Id = "new-selection",
            Name = "Selected agent",
            Address = "0x2",
        };
        await _repository.SaveAsync(new AgentsState
        {
            Agents = new List<AgentConfig> { fetchingAgent, selectedAgent },
            SelectedAgentId = fetchingAgent.Id,
        });

        // Model the race that caused the sidebar bug: the user changes selection while the
        // first agent's /info request is still in flight, then that delayed request persists.
        await _repository.SetSelectedAgentAsync(selectedAgent.Id);
        await _repository.UpdateInfoAsync(
            fetchingAgent.Id,
            "{\"model\":\"gpt-test\"}",
            "2026-08-03T00:00:00.0000000Z");

        var loaded = await _repository.LoadAsync();
        Assert.Equal(selectedAgent.Id, loaded.SelectedAgentId);
        var updated = Assert.Single(loaded.Agents, agent => agent.Id == fetchingAgent.Id);
        Assert.Equal(fetchingAgent.Name, updated.Name);
        Assert.Equal(fetchingAgent.Address, updated.Address);
        Assert.Equal("{\"model\":\"gpt-test\"}", updated.InfoJson);
        Assert.Equal("2026-08-03T00:00:00.0000000Z", updated.InfoUpdatedAt);
    }

    [Fact]
    public async Task UpdateNameAsync_ChangesOnlyTheTargetName()
    {
        var target = new AgentConfig
        {
            Id = "rename-target",
            Name = "Original",
            Address = "0x1",
            InfoJson = "{\"model\":\"kept\"}",
            InfoUpdatedAt = "2026-08-07T00:00:00Z",
        };
        var other = new AgentConfig
        {
            Id = "rename-other",
            Name = "Other",
            Address = "0x2",
        };
        await _repository.SaveAsync(new AgentsState
        {
            Agents = [target, other],
            SelectedAgentId = other.Id,
        });

        Assert.True(await _repository.UpdateNameAsync(target.Id, "  Custom Name  "));

        var loaded = await _repository.LoadAsync();
        Assert.Equal(other.Id, loaded.SelectedAgentId);
        var renamed = Assert.Single(loaded.Agents, agent => agent.Id == target.Id);
        Assert.Equal("Custom Name", renamed.Name);
        Assert.Equal(target.Address, renamed.Address);
        Assert.Equal(target.InfoJson, renamed.InfoJson);
        Assert.Equal(target.InfoUpdatedAt, renamed.InfoUpdatedAt);
        Assert.Equal("Other", Assert.Single(loaded.Agents, agent => agent.Id == other.Id).Name);
    }

    [Fact]
    public async Task UpdateNameAsync_RejectsInvalidNoOpAndMissingTargets()
    {
        var agent = new AgentConfig
        {
            Id = "rename-guard",
            Name = "Original",
            Address = "0x1",
        };
        await _repository.SaveAsync(new AgentsState { Agents = [agent] });

        Assert.False(await _repository.UpdateNameAsync(agent.Id, "  Original  "));
        Assert.False(await _repository.UpdateNameAsync(agent.Id, "   "));
        Assert.False(await _repository.UpdateNameAsync(
            agent.Id,
            new string('x', AgentConfig.MaxNameLength + 1)));
        Assert.False(await _repository.UpdateNameAsync("missing-agent", "New name"));

        Assert.Equal("Original", Assert.Single((await _repository.LoadAsync()).Agents).Name);
    }

    [Fact]
    public async Task TargetedAgentWrites_PreserveUnrelatedRowsSelectionAndCachedInfo()
    {
        var existing = new AgentConfig
        {
            Id = "targeted-existing",
            Name = "Existing",
            Address = "0x1",
            InfoJson = "{\"model\":\"keep\"}",
            InfoUpdatedAt = "2026-08-07T00:00:00Z",
        };
        await _repository.SaveAsync(new AgentsState
        {
            Agents = [existing],
            SelectedAgentId = existing.Id,
        });

        var added = new AgentConfig
        {
            Id = "targeted-added",
            Name = "Added",
            Address = "0x2",
            IconPath = "avatars/added-old.png",
        };
        Assert.True(await _repository.AppendAgentAsync(added, makeSelected: false));
        Assert.True(await _repository.UpdateIconPathAsync(added.Id, "avatars/added-new.png"));

        var loaded = await _repository.LoadAsync();
        Assert.Equal(existing.Id, loaded.SelectedAgentId);
        Assert.Equal(existing.InfoJson, Assert.Single(loaded.Agents, a => a.Id == existing.Id).InfoJson);
        Assert.Equal(
            "avatars/added-new.png",
            Assert.Single(loaded.Agents, a => a.Id == added.Id).IconPath);
    }

    [Fact]
    public async Task DeleteAgentAsync_RemovesItsWholeGraphAndKeepsOtherAgents()
    {
        var removed = new AgentConfig { Id = "atomic-agent", Name = "Remove", Address = "0x1" };
        var kept = new AgentConfig { Id = "atomic-keep", Name = "Keep", Address = "0x2" };
        await _repository.SaveAsync(new AgentsState
        {
            Agents = [removed, kept],
            SelectedAgentId = removed.Id,
        });

        await using (var connection = await AppDatabase.OpenAsync())
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO sessions (id, agent_id, title, created_at, updated_at, sort_order, mode)
                VALUES ('atomic-session', 'atomic-agent', 'Owned', 'now', 'now', 0, 'safe');
                INSERT INTO messages (id, conversation_id, role, content, created_at)
                VALUES (1, 'atomic-session', 'user', 'message', 1);
                INSERT INTO message_attachments (
                    id, conversation_id, message_id, kind, file_name, size_bytes, status, created_at)
                VALUES ('atomic-attachment', 'atomic-session', 1, 'file', 'test.txt', 1, 'sent', 1);
                INSERT INTO executions (id, conversation_id, prompt, status, created_at)
                VALUES ('atomic-execution', 'atomic-session', 'test', 'done', 1);
                INSERT INTO trace_events (id, conversation_id, execution_id, type, payload_json)
                VALUES ('atomic-trace', 'atomic-session', 'atomic-execution', 'output', '{}');
                INSERT INTO app_meta (key, value) VALUES ('active_session_id', 'atomic-session')
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                INSERT INTO app_meta (key, value) VALUES ('pinned_session_ids', '["atomic-session"]')
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        Assert.True(await _repository.DeleteAgentAsync(removed.Id, kept.Id));

        var loaded = await _repository.LoadAsync();
        Assert.Equal(kept.Id, loaded.SelectedAgentId);
        Assert.Equal(kept.Id, Assert.Single(loaded.Agents).Id);
        await using var verify = await AppDatabase.OpenAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM sessions WHERE id = 'atomic-session') +
                (SELECT COUNT(*) FROM messages WHERE conversation_id = 'atomic-session') +
                (SELECT COUNT(*) FROM message_attachments WHERE conversation_id = 'atomic-session') +
                (SELECT COUNT(*) FROM executions WHERE conversation_id = 'atomic-session') +
                (SELECT COUNT(*) FROM trace_events WHERE conversation_id = 'atomic-session');
            """;
        Assert.Equal(
            0L,
            Convert.ToInt64(
                await count.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(await AppDatabase.GetMetaAsync(verify, "active_session_id"));
        Assert.Null(await AppDatabase.GetMetaAsync(verify, "pinned_session_ids"));
    }

    [Fact]
    public async Task DeleteAgentAsync_WhenAChildDeleteFails_RollsBackEverything()
    {
        var agent = new AgentConfig { Id = "atomic-rollback", Name = "Rollback", Address = "0x1" };
        await _repository.SaveAsync(new AgentsState { Agents = [agent], SelectedAgentId = agent.Id });
        await using (var connection = await AppDatabase.OpenAsync())
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO sessions (id, agent_id, title, created_at, updated_at, sort_order, mode)
                VALUES ('atomic-rollback-session', 'atomic-rollback', 'Owned', 'now', 'now', 0, 'safe');
                INSERT INTO messages (id, conversation_id, role, content, created_at)
                VALUES (1, 'atomic-rollback-session', 'user', 'keep', 1);
                CREATE TRIGGER fail_atomic_agent_delete
                BEFORE DELETE ON messages
                WHEN OLD.conversation_id = 'atomic-rollback-session'
                BEGIN
                    SELECT RAISE(ABORT, 'forced agent delete failure');
                END;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        try
        {
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                _repository.DeleteAgentAsync(agent.Id));
        }
        finally
        {
            await using var connection = await AppDatabase.OpenAsync();
            await using var dropTrigger = connection.CreateCommand();
            dropTrigger.CommandText = "DROP TRIGGER IF EXISTS fail_atomic_agent_delete;";
            await dropTrigger.ExecuteNonQueryAsync();
        }

        var loaded = await _repository.LoadAsync();
        Assert.Equal(agent.Id, Assert.Single(loaded.Agents).Id);
        Assert.Equal(agent.Id, loaded.SelectedAgentId);
        await using var verify = await AppDatabase.OpenAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM sessions WHERE id = 'atomic-rollback-session') +
                (SELECT COUNT(*) FROM messages WHERE conversation_id = 'atomic-rollback-session');
            """;
        Assert.Equal(
            2L,
            Convert.ToInt64(
                await count.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task SaveAsync_RemovingAgent_DeletesItsConversationGraphFirst()
    {
        var agent = new AgentConfig { Id = "agent-with-session", Name = "Agent", Address = "0x1" };
        await _repository.SaveAsync(new AgentsState { Agents = new List<AgentConfig> { agent } });

        await using (var connection = await AppDatabase.OpenAsync())
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO sessions (id, agent_id, title, created_at, updated_at, sort_order, mode)
                VALUES ('owned-session', 'agent-with-session', 'Owned', 'now', 'now', 0, 'safe');

                INSERT INTO messages (id, conversation_id, role, content, created_at)
                VALUES (1, 'owned-session', 'user', 'message', 1);

                INSERT INTO message_attachments (
                    id, conversation_id, message_id, kind, file_name, size_bytes, status, created_at)
                VALUES ('owned-attachment', 'owned-session', 1, 'file', 'test.txt', 1, 'sent', 1);

                INSERT INTO executions (id, conversation_id, prompt, status, created_at)
                VALUES ('owned-execution', 'owned-session', 'test', 'complete', 1);

                INSERT INTO trace_events (id, conversation_id, execution_id, type, payload_json)
                VALUES ('owned-trace', 'owned-session', 'owned-execution', 'tool_call', '{}');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await _repository.SaveAsync(new AgentsState());

        await using var verify = await AppDatabase.OpenAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM agents WHERE id = 'agent-with-session') +
                (SELECT COUNT(*) FROM sessions WHERE id = 'owned-session') +
                (SELECT COUNT(*) FROM messages WHERE conversation_id = 'owned-session') +
                (SELECT COUNT(*) FROM message_attachments WHERE conversation_id = 'owned-session') +
                (SELECT COUNT(*) FROM executions WHERE conversation_id = 'owned-session') +
                (SELECT COUNT(*) FROM trace_events WHERE conversation_id = 'owned-session');
            """;
        Assert.Equal(
            0,
            Convert.ToInt64(
                await count.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture));
    }
}
