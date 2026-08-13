using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.IntegrationTests.Database;

[Collection(DatabaseCollection.Name)]
public sealed class SessionRepositoryTests : IAsyncLifetime
{
    private readonly TempDatabaseFixture _fixture;
    private readonly SessionRepository _sessions = new();
    private readonly ConversationRepository _messages = new();

    public SessionRepositoryTests(TempDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        // SaveAsync owns the complete session index and legitimately removes rows not present in
        // its input. Start each case with an empty index so child rows left by another class in
        // this collection cannot make that delete fail; xunit v3 may choose any class order.
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM message_attachments;
            DELETE FROM messages;
            DELETE FROM trace_events;
            DELETE FROM executions;
            DELETE FROM sessions;
            DELETE FROM app_meta;
            DELETE FROM agents;
            INSERT INTO agents (id, name, address) VALUES ('agent-1', 'Agent', '0x1');
            """;
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task SaveUpdateAndDelete_Conversation_PersistsEachLifecycleStage()
    {
        var session = Summary("session-lifecycle", "Original", "2026-01-01T00:00:00Z");
        await _sessions.SaveAsync(new SessionsState
        {
            Sessions = new List<SessionSummary> { session },
            ActiveSessionId = session.Id,
        });

        var created = await _sessions.LoadAsync();
        Assert.Equal(session.Id, created.ActiveSessionId);
        Assert.Equal("Original", Assert.Single(created.Sessions).Title);

        session.Title = "Renamed";
        session.UpdatedAt = "2026-01-02T00:00:00Z";
        await _sessions.UpdateSessionAsync(session);
        Assert.Equal("Renamed", Assert.Single((await _sessions.LoadAsync()).Sessions).Title);

        await _messages.DeleteMessagesAsync(session.Id);
        await _sessions.SaveAsync(new SessionsState());
        var deleted = await _sessions.LoadAsync();
        Assert.Empty(deleted.Sessions);
        Assert.Null(deleted.ActiveSessionId);
    }

    [Fact]
    public async Task Mode_SurvivesSaveAndUpdate_AndDefaultsToSafe()
    {
        var session = Summary("session-mode", "Modes", "2026-01-01T00:00:00Z");
        await _sessions.SaveAsync(new SessionsState { Sessions = new List<SessionSummary> { session } });

        // The mode is client-owned: the host forgets ours between turns, so a conversation that
        // reloads at the wrong mode would silently start auto-approving (or refusing) tools.
        Assert.Equal("safe", Assert.Single((await _sessions.LoadAsync()).Sessions).Mode);

        session.Mode = "plan";
        await _sessions.UpdateSessionAsync(session);
        Assert.Equal("plan", Assert.Single((await _sessions.LoadAsync()).Sessions).Mode);

        // A mode the host wouldn't honour is not a mode: it falls back rather than being sent.
        session.Mode = "yolo";
        Assert.Equal("safe", session.Mode);
    }

    [Fact]
    public async Task Attention_MarksUnreadPersistsApprovalAndClearsWhenOpened()
    {
        var session = Summary("session-attention", "Attention", "2026-01-01T00:00:00Z");
        await _sessions.AppendSessionAsync(session, makeActive: true);

        await _sessions.MarkUnreadAsync(session.Id, requiresAttention: false);
        await _sessions.MarkUnreadAsync(session.Id, requiresAttention: true);

        var unread = await _sessions.GetSessionAsync(session.Id);
        Assert.NotNull(unread);
        Assert.Equal(2, unread.UnreadCount);
        Assert.True(unread.RequiresAttention);

        await _sessions.ClearAttentionAsync(session.Id);
        var opened = await _sessions.GetSessionAsync(session.Id);
        Assert.NotNull(opened);
        Assert.Equal(0, opened.UnreadCount);
        Assert.False(opened.RequiresAttention);
    }

    /// <summary>
    /// The sidebar's rolled-up badge for a collapsed agent. It has to come from an aggregate
    /// rather than from the loaded rows, because a collapsed branch's conversations are never
    /// fetched — which is exactly the case the badge exists for.
    /// </summary>
    [Fact]
    public async Task GetAgentAttentionAsync_RollsUpPerAgent_AndOmitsAgentsWithNothingToReport()
    {
        var quiet = Summary("session-quiet", "Quiet", "2026-01-01T00:00:00Z");
        var unread = Summary("session-unread", "Unread", "2026-01-02T00:00:00Z");
        var alsoUnread = Summary("session-unread-2", "Unread too", "2026-01-03T00:00:00Z");
        var otherAgent = Summary("session-other", "Other agent", "2026-01-04T00:00:00Z");
        otherAgent.AgentId = "agent-2";
        // The shared fixture seeds only agent-1, and sessions carry a real FK to agents.
        await InsertAgentAsync("agent-2", "Second agent", "0x2");

        foreach (var session in new[] { quiet, unread, alsoUnread, otherAgent })
            await _sessions.AppendSessionAsync(session, makeActive: false);

        await _sessions.MarkUnreadAsync(unread.Id, requiresAttention: false);
        await _sessions.MarkUnreadAsync(unread.Id, requiresAttention: false);
        await _sessions.MarkUnreadAsync(alsoUnread.Id, requiresAttention: true);
        await _sessions.MarkUnreadAsync(otherAgent.Id, requiresAttention: false);

        var attention = await _sessions.GetAgentAttentionAsync();

        // Summed across the agent's conversations, and attention is true when *any* of them wants
        // an approval — the quiet conversation must not dilute either.
        Assert.Equal(3, attention["agent-1"].UnreadCount);
        Assert.True(attention["agent-1"].RequiresAttention);
        Assert.Equal(1, attention["agent-2"].UnreadCount);
        Assert.False(attention["agent-2"].RequiresAttention);

        // An all-read sidebar is the common case and must cost an empty dictionary, not a row of
        // zeroes per agent — the rollup badge keys off presence in this map.
        await _sessions.ClearAttentionAsync(unread.Id);
        await _sessions.ClearAttentionAsync(alsoUnread.Id);
        await _sessions.ClearAttentionAsync(otherAgent.Id);
        Assert.Empty(await _sessions.GetAgentAttentionAsync());
    }

    [Fact]
    public async Task LoadAsync_SameSortOrder_OrdersByUpdatedAtDescending()
    {
        await InsertSessionAsync("order-old", "Old", "2026-01-01T00:00:00Z", sortOrder: 50);
        await InsertSessionAsync("order-new", "New", "2026-03-01T00:00:00Z", sortOrder: 50);
        await InsertSessionAsync("order-middle", "Middle", "2026-02-01T00:00:00Z", sortOrder: 50);

        var state = await _sessions.LoadAsync();
        var ordered = state.Sessions
            .Where(session => session.Id.StartsWith("order-", StringComparison.Ordinal))
            .Select(session => session.Id);

        Assert.Equal(new[] { "order-new", "order-middle", "order-old" }, ordered);
    }

    [Fact]
    public async Task SaveAsync_PinnedAndActiveSession_RoundTripsMetadata()
    {
        var pinned = Summary("pinned-session", "Pinned", "2026-04-01T00:00:00Z");
        pinned.IsPinned = true;

        await _sessions.SaveAsync(new SessionsState
        {
            Sessions = new List<SessionSummary> { pinned },
            ActiveSessionId = pinned.Id,
        });

        var loaded = await _sessions.LoadAsync();
        var actual = Assert.Single(loaded.Sessions);
        Assert.True(actual.IsPinned);
        Assert.Equal(pinned.Id, loaded.ActiveSessionId);
    }

    [Fact]
    public async Task SaveAsync_InvalidSession_SkipsRowAndClearsStaleActivePointer()
    {
        await _sessions.SaveAsync(new SessionsState
        {
            Sessions = new List<SessionSummary>
            {
                Summary("", "Missing id", "2026-01-01T00:00:00Z"),
                Summary("invalid-title", "", "2026-01-01T00:00:00Z"),
            },
            ActiveSessionId = "missing",
        });

        var loaded = await _sessions.LoadAsync();
        Assert.Empty(loaded.Sessions);
        Assert.Null(loaded.ActiveSessionId);
    }

    [Fact]
    public async Task AppendSession_AddsOneRowAndActivatesIt_WithoutDisturbingTheIndex()
    {
        var first = Summary("append-first", "First", "2026-01-01T00:00:00Z");
        var second = Summary("append-second", "Second", "2026-01-02T00:00:00Z");
        second.IsPinned = true;
        await _sessions.SaveAsync(new SessionsState
        {
            Sessions = new List<SessionSummary> { first, second },
            ActiveSessionId = first.Id,
        });

        var appended = Summary("append-third", "Third", "2026-01-03T00:00:00Z");
        await _sessions.AppendSessionAsync(appended);

        var state = await _sessions.LoadAsync();
        // The point of the targeted write: the two existing rows are untouched, including the
        // pinned flag that lives outside the sessions table entirely.
        Assert.Equal(3, state.Sessions.Count);
        Assert.Equal(appended.Id, state.ActiveSessionId);
        Assert.True(state.Sessions.Single(s => s.Id == second.Id).IsPinned);
        Assert.False(state.Sessions.Single(s => s.Id == appended.Id).IsPinned);
        // sort_order continues the sequence, so LoadAsync's ORDER BY puts the new row last —
        // matching where the caller's in-memory Sessions.Add placed it.
        Assert.Equal(appended.Id, state.Sessions[^1].Id);

        await _messages.DeleteMessagesAsync(first.Id);
        await _sessions.SaveAsync(new SessionsState());
    }

    [Fact]
    public async Task AppendSession_WithoutMakeActive_LeavesTheActivePointerAlone()
    {
        var existing = Summary("append-keep-active", "Existing", "2026-01-01T00:00:00Z");
        await _sessions.SaveAsync(new SessionsState
        {
            Sessions = new List<SessionSummary> { existing },
            ActiveSessionId = existing.Id,
        });

        await _sessions.AppendSessionAsync(
            Summary("append-inactive", "Inactive", "2026-01-02T00:00:00Z"),
            makeActive: false);

        Assert.Equal(existing.Id, (await _sessions.LoadAsync()).ActiveSessionId);
        await _sessions.SaveAsync(new SessionsState());
    }

    [Fact]
    public async Task SetPinned_TogglesOneConversation_AndClearsTheMetadataRowWhenEmpty()
    {
        var pinned = Summary("pin-target", "Pinned", "2026-01-01T00:00:00Z");
        var other = Summary("pin-other", "Other", "2026-01-02T00:00:00Z");
        await _sessions.SaveAsync(new SessionsState
        {
            Sessions = new List<SessionSummary> { pinned, other },
        });

        await _sessions.SetPinnedAsync(pinned.Id, isPinned: true);
        var afterPin = await _sessions.LoadAsync();
        Assert.True(afterPin.Sessions.Single(s => s.Id == pinned.Id).IsPinned);
        Assert.False(afterPin.Sessions.Single(s => s.Id == other.Id).IsPinned);

        await _sessions.SetPinnedAsync(pinned.Id, isPinned: false);
        Assert.All((await _sessions.LoadAsync()).Sessions, s => Assert.False(s.IsPinned));

        await _sessions.SaveAsync(new SessionsState());
    }

    /// <summary>
    /// The guard for the whole paging change: a delete must touch exactly the conversation asked
    /// for.
    ///
    /// <para>The production delete paths used to remove one entry from a loaded index and hand the
    /// remainder to <see cref="SessionRepository.SaveAsync"/>, which reconciles the whole table.
    /// That is correct only while the loaded list really is every conversation — and paging makes
    /// it a page. This test fails loudly against that shape the moment a read is bounded, which is
    /// precisely the silent data loss it exists to prevent.</para>
    /// </summary>
    [Fact]
    public async Task DeleteSessionAsync_RemovesOnlyThatConversation_AndItsPinAndActivePointer()
    {
        await SeedAsync(("keep-a", "2026-01-03T00:00:00Z"), ("drop", "2026-01-02T00:00:00Z"), ("keep-b", "2026-01-01T00:00:00Z"));
        await _messages.UpsertMessagesAsync("drop", [new ChatMessage
        {
            Id = 1,
            Role = ChatRole.User,
            Content = "delete the whole graph",
        }]);
        await using (var connection = await AppDatabase.OpenAsync())
        await using (var seedGraph = connection.CreateCommand())
        {
            seedGraph.CommandText = """
                INSERT INTO executions (id, conversation_id, prompt, status, created_at)
                VALUES ('drop-execution', 'drop', 'prompt', 'done', 1);
                INSERT INTO trace_events (id, conversation_id, execution_id, type, payload_json)
                VALUES ('drop-trace', 'drop', 'drop-execution', 'output', '{}');
                """;
            await seedGraph.ExecuteNonQueryAsync();
        }
        await _sessions.SetPinnedAsync("drop", true);
        await _sessions.SetPinnedAsync("keep-a", true);
        await _sessions.SetActiveSessionAsync("drop");

        await _sessions.DeleteSessionAsync("drop");

        var remaining = await _sessions.LoadAsync();
        Assert.Equal(new[] { "keep-a", "keep-b" }, remaining.Sessions.Select(s => s.Id).Order());
        // The pointer went with the row rather than dangling; picking a successor is the caller's.
        Assert.Null(remaining.ActiveSessionId);
        // The pin list is a JSON blob with no referential integrity of its own, so it is pruned by
        // hand — and only the deleted conversation's pin.
        Assert.Equal(new[] { "keep-a" }, (await _sessions.LoadPinnedAsync()).Select(s => s.Id));
        Assert.Empty(await _messages.LoadMessagesAsync("drop"));
        await using var verify = await AppDatabase.OpenAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM executions WHERE conversation_id = 'drop') +
                (SELECT COUNT(*) FROM trace_events WHERE conversation_id = 'drop');
            """;
        Assert.Equal(
            0L,
            Convert.ToInt64(
                await count.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task DeleteSessionAsync_WhenAChildDeleteFails_RollsBackTheWholeGraph()
    {
        await SeedAsync(("rollback-delete", "2026-01-01T00:00:00Z"));
        await _messages.UpsertMessagesAsync("rollback-delete", [new ChatMessage
        {
            Id = 1,
            Role = ChatRole.User,
            Content = "keep every row",
        }]);

        await using (var connection = await AppDatabase.OpenAsync())
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER fail_rollback_message_delete
                BEFORE DELETE ON messages
                WHEN OLD.conversation_id = 'rollback-delete'
                BEGIN
                    SELECT RAISE(ABORT, 'forced delete failure');
                END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        try
        {
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                _sessions.DeleteSessionAsync("rollback-delete"));
        }
        finally
        {
            await using var connection = await AppDatabase.OpenAsync();
            await using var dropTrigger = connection.CreateCommand();
            dropTrigger.CommandText = "DROP TRIGGER IF EXISTS fail_rollback_message_delete;";
            await dropTrigger.ExecuteNonQueryAsync();
        }

        Assert.NotNull(await _sessions.GetSessionAsync("rollback-delete"));
        Assert.Single(await _messages.LoadMessagesAsync("rollback-delete"));
    }

    [Fact]
    public async Task DeleteSessionsForAgentAsync_RemovesOnlyThatAgentsConversations()
    {
        await InsertAgentAsync("agent-2");
        await SeedAsync(("a1", "2026-01-02T00:00:00Z"), ("a2", "2026-01-01T00:00:00Z"));
        await InsertSessionForAgentAsync("b1", "agent-2", "2026-01-03T00:00:00Z");
        await _sessions.SetActiveSessionAsync("a1");

        await _sessions.DeleteSessionsForAgentAsync("agent-1");

        Assert.Equal(new[] { "b1" }, (await _sessions.LoadAsync()).Sessions.Select(s => s.Id));
        Assert.Null(await _sessions.GetActiveSessionIdAsync());
        Assert.Equal(0, await _sessions.CountForAgentAsync("agent-1"));
        Assert.Equal(1, await _sessions.CountForAgentAsync("agent-2"));
    }

    [Fact]
    public async Task GetSessionAsync_ReturnsTheRowOrNull_WithPinStateAttached()
    {
        await SeedAsync(("present", "2026-01-01T00:00:00Z"));
        await _sessions.SetPinnedAsync("present", true);

        var found = await _sessions.GetSessionAsync("present");
        Assert.NotNull(found);
        Assert.Equal("present", found!.Id);
        Assert.True(found.IsPinned);
        Assert.Null(await _sessions.GetSessionAsync("absent"));
    }

    /// <summary>
    /// Keyset paging, newest first, with the extra-row probe answering <c>HasMore</c>.
    /// <para>The cursor carries <c>id</c> as well as <c>updated_at</c> precisely so conversations
    /// sharing a timestamp cannot straddle a page boundary and lose one — which is what the
    /// same-timestamp pair here checks.</para>
    /// </summary>
    [Fact]
    public async Task LoadAgentSessionsAsync_PagesNewestFirst_WithoutDroppingTiedTimestamps()
    {
        await SeedAsync(
            ("s5", "2026-01-05T00:00:00Z"),
            ("s4", "2026-01-04T00:00:00Z"),
            ("s3b", "2026-01-03T00:00:00Z"),
            ("s3a", "2026-01-03T00:00:00Z"),
            ("s1", "2026-01-01T00:00:00Z"));

        var walked = new List<string>();
        var page = await _sessions.LoadAgentSessionsAsync("agent-1", limit: 2);
        while (true)
        {
            walked.AddRange(page.Sessions.Select(s => s.Id));
            if (page.NextCursor is not { } cursor) break;
            page = await _sessions.LoadAgentSessionsAsync("agent-1", limit: 2, after: cursor);
        }

        // Every conversation, once, newest first — the tied pair broken by id descending.
        Assert.Equal(new[] { "s5", "s4", "s3b", "s3a", "s1" }, walked);
        Assert.Equal(walked.Distinct().Count(), walked.Count);

        var first = await _sessions.LoadAgentSessionsAsync("agent-1", limit: 2);
        Assert.True(first.HasMore);
        Assert.Equal(2, first.Sessions.Count);
        var whole = await _sessions.LoadAgentSessionsAsync("agent-1", limit: 5);
        Assert.False(whole.HasMore);
        Assert.Null(whole.NextCursor);
    }

    /// <summary>
    /// <c>ResolveForAgentAsync</c> is the SQL twin of the pure <c>SessionSelection.FindExisting</c>,
    /// and this is what stops the two from drifting: both are asked the same questions over the
    /// same data and must agree. The pure function's own tests remain the statement of the rule.
    /// </summary>
    [Fact]
    public async Task ResolveForAgentAsync_AgreesWithSessionSelection()
    {
        await InsertAgentAsync("agent-2");
        await SeedAsync(("older", "2026-01-01T00:00:00Z"), ("newer", "2026-01-02T00:00:00Z"));
        await InsertSessionForAgentAsync("other-agent", "agent-2", "2026-01-03T00:00:00Z");
        var all = (await _sessions.LoadAsync()).Sessions;

        foreach (var activeId in new string?[] { null, "older", "newer", "other-agent", "absent" })
        {
            await _sessions.SetActiveSessionAsync(activeId);
            foreach (var agentId in new[] { "agent-1", "agent-2", "agent-unknown" })
            {
                var expected = SessionSelection.FindExisting(all, activeId, agentId);
                var actual = await _sessions.ResolveForAgentAsync(agentId);
                Assert.Equal(expected?.Id, actual?.Id);
            }
        }
    }

    [Fact]
    public async Task SearchByTitleAsync_MatchesSubstrings_AndTreatsWildcardsAsLiterals()
    {
        await SeedAsync(("plain", "2026-01-03T00:00:00Z"), ("percent", "2026-01-02T00:00:00Z"), ("under", "2026-01-01T00:00:00Z"));
        await RenameAsync("plain", "Migration notes");
        await RenameAsync("percent", "100% done");
        await RenameAsync("under", "snake_case");

        Assert.Equal(new[] { "plain" }, (await _sessions.SearchByTitleAsync("migration")).Select(s => s.Id));
        // A literal % must not behave as "match anything", which is what an unescaped LIKE would do.
        Assert.Equal(new[] { "percent" }, (await _sessions.SearchByTitleAsync("100%")).Select(s => s.Id));
        Assert.Equal(new[] { "under" }, (await _sessions.SearchByTitleAsync("snake_")).Select(s => s.Id));
        Assert.Empty(await _sessions.SearchByTitleAsync("nothing here"));
        Assert.Empty(await _sessions.SearchByTitleAsync("   "));
    }

    [Fact]
    public async Task LoadRecentAsync_ReturnsNewestAcrossAgents_BoundedByLimit()
    {
        await InsertAgentAsync("agent-2");
        await SeedAsync(("a-old", "2026-01-01T00:00:00Z"), ("a-new", "2026-01-04T00:00:00Z"));
        await InsertSessionForAgentAsync("b-mid", "agent-2", "2026-01-03T00:00:00Z");

        Assert.Equal(new[] { "a-new", "b-mid" }, (await _sessions.LoadRecentAsync(2)).Select(s => s.Id));
        Assert.Equal(3, (await _sessions.LoadRecentAsync(10)).Count);
    }

    private async Task SeedAsync(params (string Id, string UpdatedAt)[] sessions)
    {
        foreach (var (id, updatedAt) in sessions)
            await _sessions.AppendSessionAsync(Summary(id, id, updatedAt), makeActive: false);
    }

    private async Task RenameAsync(string id, string title)
    {
        var session = await _sessions.GetSessionAsync(id);
        session!.Title = title;
        await _sessions.UpdateSessionAsync(session);
    }

    private static async Task InsertAgentAsync(string agentId)
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO agents (id, name, address) VALUES ($id, $id, '0x2');";
        command.Parameters.AddWithValue("$id", agentId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertSessionForAgentAsync(string id, string agentId, string updatedAt)
    {
        var summary = Summary(id, id, updatedAt);
        summary.AgentId = agentId;
        await _sessions.AppendSessionAsync(summary, makeActive: false);
    }

    private static SessionSummary Summary(string id, string title, string updatedAt) => new()
    {
        Id = id,
        AgentId = "agent-1",
        Title = title,
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = updatedAt,
    };

    private static async Task InsertAgentAsync(string id, string name, string address)
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO agents (id, name, address) VALUES ($id, $name, $address);";
        AppDatabase.Add(command, "$id", id);
        AppDatabase.Add(command, "$name", name);
        AppDatabase.Add(command, "$address", address);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertSessionAsync(string id, string title, string updatedAt, int sortOrder)
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (id, agent_id, title, created_at, updated_at, sort_order)
            VALUES ($id, 'agent-1', $title, '2026-01-01T00:00:00Z', $updated, $sort);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$updated", updatedAt);
        command.Parameters.AddWithValue("$sort", sortOrder);
        await command.ExecuteNonQueryAsync();
    }
}
