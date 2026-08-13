using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using Microsoft.Data.Sqlite;

namespace ConnectOnion.IntegrationTests.Database;

[Collection(DatabaseCollection.Name)]
public sealed class ConversationRepositoryTests
{
    private readonly TempDatabaseFixture _fixture;
    private readonly ConversationRepository _repository = new();

    public ConversationRepositoryTests(TempDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task UpsertMessagesAsync_WhenCommitFails_PropagatesTheFailure()
    {
        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            _repository.UpsertMessagesAsync(
                "missing-conversation",
                new[] { Message(1, "must not look persisted") }));

        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Empty(await _repository.LoadMessagesAsync("missing-conversation"));
    }

    [Fact]
    public async Task DeleteMessageAsync_WhenSqliteRejectsDelete_PropagatesAndKeepsTheBubble()
    {
        const string conversationId = "delete-propagates";
        const string triggerName = "test_abort_message_delete";
        await _fixture.CreateSessionAsync(conversationId);
        await _repository.UpsertMessagesAsync(
            conversationId,
            [Message(1, "keep after failure", Attachment("keep-after-failure"))]);

        await ExecuteAsync($"""
            CREATE TRIGGER {triggerName}
            BEFORE DELETE ON messages
            WHEN OLD.conversation_id = '{conversationId}'
            BEGIN
                SELECT RAISE(ABORT, 'forced delete failure');
            END;
            """);

        try
        {
            var exception = await Assert.ThrowsAsync<SqliteException>(() =>
                _repository.DeleteMessageAsync(conversationId, 1));

            Assert.Equal(19, exception.SqliteErrorCode);
            var message = Assert.Single(await _repository.LoadMessagesAsync(conversationId));
            Assert.Equal("keep after failure", message.Content);
            Assert.Equal("keep-after-failure", Assert.Single(message.Attachments).Id);
        }
        finally
        {
            await ExecuteAsync($"DROP TRIGGER IF EXISTS {triggerName};");
        }
    }

    [Fact]
    public async Task UpsertMessagesAsync_SubsetUpdate_LeavesOtherRowsUnchanged()
    {
        await _fixture.CreateSessionAsync();
        await _repository.UpsertMessagesAsync("conversation-1", new[]
        {
            Message(1, "keep me"),
            Message(2, "old value"),
        });

        await _repository.UpsertMessagesAsync("conversation-1", new[] { Message(2, "new value") });

        var messages = await _repository.LoadMessagesAsync("conversation-1");
        Assert.Collection(messages,
            first => Assert.Equal("keep me", first.Content),
            second => Assert.Equal("new value", second.Content));
    }

    [Fact]
    public async Task UpsertMessagesAsync_ExistingMessage_PreservesCreatedAt()
    {
        await _fixture.CreateSessionAsync("created-at");
        await _repository.UpsertMessagesAsync("created-at", new[] { Message(1, "before") });
        var createdAt = await ReadCreatedAtAsync("created-at", 1);

        await Task.Delay(10);
        await _repository.UpsertMessagesAsync("created-at", new[] { Message(1, "after") });

        Assert.Equal(createdAt, await ReadCreatedAtAsync("created-at", 1));
    }

    [Fact]
    public async Task UpsertMessagesAsync_ChangedAttachments_ReplacesOnlyTargetMessageAttachments()
    {
        await _fixture.CreateSessionAsync("attachments");
        var first = Message(1, "first", Attachment("old-1"), Attachment("old-2"));
        var second = Message(2, "second", Attachment("keep"));
        await _repository.UpsertMessagesAsync("attachments", new[] { first, second });

        await _repository.UpsertMessagesAsync("attachments", new[]
        {
            Message(1, "first", Attachment("replacement")),
        });

        var messages = await _repository.LoadMessagesAsync("attachments");
        Assert.Equal(new[] { "replacement" }, messages[0].Attachments.Select(a => a.Id));
        Assert.Equal(new[] { "keep" }, messages[1].Attachments.Select(a => a.Id));
    }

    [Fact]
    public async Task UpsertMessagesAsync_Attachment_DoesNotPersistBase64Payload()
    {
        await _fixture.CreateSessionAsync("no-payload");
        await _repository.UpsertMessagesAsync("no-payload", new[]
        {
            Message(1, "image", new ChatAttachment
            {
                Id = "image-1",
                Kind = AttachmentKind.Image,
                FileName = "image.png",
                MimeType = "image/png",
                LocalCachePath = "C:\\cache\\image.png",
                RemoteUri = "https://example.test/image.png",
            }),
        });

        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file_name, mime_type, local_cache_path, remote_uri
            FROM message_attachments
            WHERE conversation_id = 'no-payload' AND message_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var persisted = string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetString));
        Assert.DoesNotContain("data:image/png;base64", persisted, StringComparison.Ordinal);
        Assert.Equal("image.png", reader.GetString(0));
    }

    [Fact]
    public async Task UpsertMessagesAsync_OutOfOrderIds_LoadsInAscendingIdOrder()
    {
        await _fixture.CreateSessionAsync("ordering");
        await _repository.UpsertMessagesAsync("ordering", new[]
        {
            Message(100, "last"),
            Message(2, "middle"),
            Message(1, "first"),
        });

        var messages = await _repository.LoadMessagesAsync("ordering");

        Assert.Equal(new long[] { 1, 2, 100 }, messages.Select(message => message.Id));
    }

    [Fact]
    public async Task PagedReads_ReturnNewestThenOlderSlices_WithScopedAttachments()
    {
        await _fixture.CreateSessionAsync("paging");
        await _repository.UpsertMessagesAsync(
            "paging",
            Enumerable.Range(1, 9)
                .Select(id => Message(id, $"message-{id}", Attachment($"attachment-{id}")))
                .ToArray());

        var newest = await _repository.LoadRecentMessagesAsync("paging", pageSize: 4);
        var older = await _repository.LoadMessagesBeforeAsync("paging", newest.Messages[0].Id, pageSize: 4);
        var oldest = await _repository.LoadMessagesBeforeAsync("paging", older.Messages[0].Id, pageSize: 4);

        Assert.True(newest.HasMoreBefore);
        Assert.Equal(new long[] { 6, 7, 8, 9 }, newest.Messages.Select(message => message.Id));
        Assert.All(newest.Messages, message => Assert.StartsWith("attachment-", Assert.Single(message.Attachments).Id));
        Assert.True(older.HasMoreBefore);
        Assert.Equal(new long[] { 2, 3, 4, 5 }, older.Messages.Select(message => message.Id));
        Assert.False(oldest.HasMoreBefore);
        Assert.Equal(new long[] { 1 }, oldest.Messages.Select(message => message.Id));
    }

    [Fact]
    public async Task TranscriptSearch_UsesFtsIndex_AndTracksMessageChanges()
    {
        await _fixture.CreateSessionAsync("search-one");
        await _fixture.CreateSessionAsync("search-two");
        await _repository.UpsertMessagesAsync("search-one", new[] { Message(1, "alpha needle omega") });
        await _repository.UpsertMessagesAsync("search-two", new[] { Message(1, "nothing here") });

        var initial = await _repository.SearchMessageContentAsync("needle");
        Assert.Equal("alpha needle omega", initial["search-one"]);
        Assert.DoesNotContain("search-two", initial.Keys);
        Assert.Empty(await _repository.SearchMessageContentAsync("ne"));

        await _repository.UpsertMessagesAsync("search-one", new[] { Message(1, "replacement text") });
        Assert.Empty(await _repository.SearchMessageContentAsync("needle"));
        Assert.Equal("replacement text", (await _repository.SearchMessageContentAsync("place"))["search-one"]);

        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT rowid FROM message_search WHERE message_search MATCH 'needle';";
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, detail => detail.Contains("VIRTUAL TABLE INDEX", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The search index is maintained by rowid, and deleting a conversation must not scan it.
    ///
    /// <para>Schema v8 removed FTS rows with
    /// <c>WHERE conversation_id = ... AND message_id = ...</c>. Both columns are UNINDEXED on the
    /// fts5 table and a non-MATCH constraint there has no index to use, so the plan was a full
    /// <c>SCAN message_search VIRTUAL TABLE</c> — per deleted row, because the trigger is per-row.
    /// Deleting one conversation was therefore O(its messages x every message in the database):
    /// measured at 1.03 s for a 500-message conversation in a 20k-message database. v9 addresses
    /// the row by rowid instead, via <c>message_search_map</c>.</para>
    ///
    /// <para>The plan assertion is the point of this test. The correctness half would pass just as
    /// well against the v8 triggers, so without it a revert to the scanning form would be silent.</para>
    /// </summary>
    [Fact]
    public async Task TranscriptSearchIndex_IsMaintainedByRowid_AndStaysAlignedAcrossEdits()
    {
        await _fixture.CreateSessionAsync("fts-keep");
        await _fixture.CreateSessionAsync("fts-drop");
        await _repository.UpsertMessagesAsync("fts-keep", new[] { Message(1, "keep me findable") });
        await _repository.UpsertMessagesAsync(
            "fts-drop", new[] { Message(1, "doomed alpha"), Message(2, "doomed beta") });

        await using var connection = await AppDatabase.OpenAsync();

        // The delete trigger's lookup resolves through the map's real index...
        Assert.Contains(
            await QueryPlanAsync(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT fts_rowid FROM message_search_map
                WHERE conversation_id = 'fts-drop' AND message_id = 1;
                """),
            detail => detail.Contains("ux_message_search_map", StringComparison.OrdinalIgnoreCase));

        // ...and the FTS removal it feeds is a rowid seek, never a scan of the index.
        var deletePlan = await QueryPlanAsync(
            connection, "EXPLAIN QUERY PLAN DELETE FROM message_search WHERE rowid = 1;");
        Assert.DoesNotContain(
            deletePlan,
            detail => detail.Contains("SCAN message_search", StringComparison.OrdinalIgnoreCase)
                      && !detail.Contains(":=", StringComparison.Ordinal));

        await _repository.DeleteMessagesAsync("fts-drop");

        Assert.Empty(await _repository.SearchMessageContentAsync("doomed"));
        Assert.Equal("keep me findable", (await _repository.SearchMessageContentAsync("findable"))["fts-keep"]);

        // Every surviving FTS row is still described by the map row that names it — the pair is
        // what the triggers keep in step, and a drift here is how a stale excerpt would surface.
        Assert.Equal(0, await ScalarAsync(connection, """
            SELECT COUNT(*) FROM message_search_map AS map
            JOIN message_search AS search ON search.rowid = map.fts_rowid
            WHERE search.conversation_id <> map.conversation_id
               OR search.message_id <> map.message_id;
            """));
        Assert.Equal(0, await ScalarAsync(connection, """
            SELECT (SELECT COUNT(*) FROM message_search) - (SELECT COUNT(*) FROM message_search_map);
            """));
    }

    private static async Task<List<string>> QueryPlanAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        return plan;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task LoadLastMessagesAsync_ReturnsOneNewestVisibleMessagePerRequestedConversation()
    {
        await _fixture.CreateSessionAsync("preview-one");
        await _fixture.CreateSessionAsync("preview-two");
        await _fixture.CreateSessionAsync("preview-ignored");
        await _repository.UpsertMessagesAsync("preview-one", new[] { Message(1, "old"), Message(2, "new") });
        await _repository.UpsertMessagesAsync("preview-two", new[] { Message(1, "second") });
        await _repository.UpsertMessagesAsync("preview-ignored", new[] { Message(1, "ignored") });

        var messages = await _repository.LoadLastMessagesAsync(new[] { "preview-one", "preview-two" });

        Assert.Equal(2, messages.Count);
        Assert.Equal("new", messages["preview-one"].Content);
        Assert.Equal("second", messages["preview-two"].Content);
        Assert.DoesNotContain("preview-ignored", messages.Keys);
    }

    [Fact]
    public async Task GetNextMessageIdAsync_EmptyAndPopulatedConversation_ReturnsMaxPlusOne()
    {
        await _fixture.CreateSessionAsync("next-id");
        Assert.Equal(1, await _repository.GetNextMessageIdAsync("next-id"));
        await _repository.UpsertMessagesAsync("next-id", new[] { Message(5, "five"), Message(100, "hundred") });

        Assert.Equal(101, await _repository.GetNextMessageIdAsync("next-id"));
    }

    [Fact]
    public async Task LoadLastAgentMessageAsync_MixedRoles_ReturnsLatestAgentWithAttachments()
    {
        await _fixture.CreateSessionAsync("last-agent");
        await _repository.UpsertMessagesAsync("last-agent", new[]
        {
            Message(1, "user"),
            AgentMessage(2, "older", Attachment("old-agent-file")),
            AgentMessage(3, "latest", Attachment("latest-agent-file")),
            Message(4, "newer user"),
        });

        var message = await _repository.LoadLastAgentMessageAsync("last-agent");

        Assert.NotNull(message);
        Assert.Equal(3, message.Id);
        Assert.Equal("latest", message.Content);
        Assert.Equal("latest-agent-file", Assert.Single(message.Attachments).Id);
    }

    [Fact]
    public async Task DeleteMessagesAsync_Conversation_RemovesMessagesAndAttachmentsOnlyForTarget()
    {
        await _fixture.CreateSessionAsync("delete-target");
        await _fixture.CreateSessionAsync("delete-keep");
        await _repository.UpsertMessagesAsync("delete-target", new[] { Message(1, "delete", Attachment("delete-file")) });
        await _repository.UpsertMessagesAsync("delete-keep", new[] { Message(1, "keep", Attachment("keep-file-2")) });

        await _repository.DeleteMessagesAsync("delete-target");

        Assert.Empty(await _repository.LoadMessagesAsync("delete-target"));
        var kept = Assert.Single(await _repository.LoadMessagesAsync("delete-keep"));
        Assert.Equal("keep-file-2", Assert.Single(kept.Attachments).Id);
    }

    [Fact]
    public async Task DeleteMessageAsync_RemovesOnlyThatBubbleAndItsAttachments()
    {
        // What un-sending a queued message does: the row was written at submit time, so backing
        // out has to take it — and nothing else — away again.
        await _fixture.CreateSessionAsync("unsend");
        await _repository.UpsertMessagesAsync("unsend", new[]
        {
            Message(1, "earlier", Attachment("keep-file")),
            Message(2, "cancelled", Attachment("drop-file")),
        });

        await _repository.DeleteMessageAsync("unsend", 2);

        var remaining = Assert.Single(await _repository.LoadMessagesAsync("unsend"));
        Assert.Equal(1, remaining.Id);
        Assert.Equal("keep-file", Assert.Single(remaining.Attachments).Id);
    }

    [Fact]
    public async Task DeleteMessageAsync_MissingRow_IsANoOp()
    {
        // The cancel path races the run actually starting; losing that race must leave the
        // conversation untouched rather than throwing.
        await _fixture.CreateSessionAsync("unsend-missing");
        await _repository.UpsertMessagesAsync("unsend-missing", new[] { Message(1, "kept") });

        await _repository.DeleteMessageAsync("unsend-missing", 99);

        Assert.Single(await _repository.LoadMessagesAsync("unsend-missing"));
    }

    [Fact]
    public async Task UpsertMessagesAsync_EventFields_RoundTripsPersistedSnapshot()
    {
        await _fixture.CreateSessionAsync("event-roundtrip");
        var expected = new ChatMessage
        {
            Id = 7,
            Role = ChatRole.Event,
            Content = "content",
            AgentName = "Agent",
            // Any kind but "approval" — those are filtered out of every read (see below).
            EventKind = "plan_review",
            EventKey = "event-key",
            EventEyebrow = "Plan review",
            EventTitle = "Run command",
            EventDetail = "description",
            EventMeta = "Approved once",
            EventArgs = "{\"command\":\"echo test\"}",
            EventResult = "ok",
            Status = EventStatus.Done,
            IsOnboarding = true,
        };
        Assert.True(expected.IsInteractiveCardExpanded);
        await _repository.UpsertMessagesAsync("event-roundtrip", new[] { expected });

        var actual = Assert.Single(await _repository.LoadMessagesAsync("event-roundtrip"));

        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Role, actual.Role);
        Assert.Equal(expected.Content, actual.Content);
        Assert.Equal(expected.AgentName, actual.AgentName);
        Assert.Equal(expected.EventKind, actual.EventKind);
        Assert.Equal(expected.EventKey, actual.EventKey);
        Assert.Equal(expected.EventEyebrow, actual.EventEyebrow);
        Assert.Equal(expected.EventTitle, actual.EventTitle);
        Assert.Equal(expected.EventDetail, actual.EventDetail);
        Assert.Equal(expected.EventMeta, actual.EventMeta);
        Assert.Equal(expected.EventArgs, actual.EventArgs);
        Assert.Equal(expected.EventResult, actual.EventResult);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.IsOnboarding, actual.IsOnboarding);
        Assert.False(actual.IsInteractiveCardExpanded);
    }

    [Fact]
    public async Task UpsertMessagesAsync_ToolActivity_RoundTripsWithTrimSafeMetadata()
    {
        const string conversationId = "tool-activity-roundtrip";
        await _fixture.CreateSessionAsync(conversationId);
        var activity = new ToolActivityViewModel
        {
            TurnId = "turn-1",
            Status = ToolActivityStatus.Success,
            DisplayMode = ToolDisplayMode.Compact,
            Summary = "2 tools completed",
            IsExpanded = false,
        };
        activity.Steps.Add(new ToolStepViewModel
        {
            Id = "step-1",
            Sequence = 1,
            ToolName = "search",
            DisplayName = "Search",
            Summary = "Found files",
            Status = ToolStepStatus.Success,
            DurationMs = 12,
        });
        activity.Steps.Add(new ToolStepViewModel
        {
            Id = "step-2",
            Sequence = 2,
            ToolName = "read_file",
            DisplayName = "Read file",
            Summary = "Loaded source",
            Status = ToolStepStatus.Success,
            DurationMs = 18,
        });

        await _repository.UpsertMessagesAsync(conversationId,
        [
            new ChatMessage
            {
                Id = 1,
                Role = ChatRole.Event,
                EventKind = "tool_activity",
                EventTitle = "Tool execution",
                ToolActivity = activity,
                Status = EventStatus.Done,
            },
        ]);

        var restored = Assert.Single(await _repository.LoadMessagesAsync(conversationId));
        Assert.Equal("tool_activity", restored.EventKind);
        Assert.NotNull(restored.ToolActivity);
        Assert.Equal("turn-1", restored.ToolActivity.TurnId);
        Assert.Equal(ToolActivityStatus.Success, restored.ToolActivity.Status);
        Assert.Collection(
            restored.ToolActivity.Steps,
            first =>
            {
                Assert.Equal("search", first.ToolName);
                Assert.Equal("Found files", first.Summary);
            },
            second =>
            {
                Assert.Equal("read_file", second.ToolName);
                Assert.Equal(18, second.DurationMs);
            });
    }

    [Theory]
    [InlineData(DiffChangeState.Applied)]
    [InlineData(DiffChangeState.Rejected)]
    [InlineData(DiffChangeState.Pending)]
    [InlineData(DiffChangeState.Failed)]
    public async Task HistoricalDiff_RoundTripsFolded(DiffChangeState state)
    {
        var conversationId = $"diff-{state}";
        await _fixture.CreateSessionAsync(conversationId);
        var diff = new ChatMessage
        {
            Id = 1,
            Role = ChatRole.Event,
            EventKind = "diff_preview",
            EventTitle = "/tmp/test.txt",
            EventEyebrow = "CREATE",
            EventDetail = "+ hello world",
            Status = EventStatus.Done,
        };
        diff.SetDiffState(state);

        await _repository.UpsertMessagesAsync(conversationId, new[] { diff });
        var restored = Assert.Single(await _repository.LoadMessagesAsync(conversationId));

        Assert.Equal(state, restored.DiffState);
        Assert.Equal("+ hello world", restored.EventDetail);
        Assert.Equal(1, restored.DiffPreview.Additions);
        Assert.False(restored.IsDiffExpanded);
    }

    [Fact]
    public async Task Reads_ExcludeApprovalBubbles_EvenWhenARowExists()
    {
        // The persist path no longer writes these, but rows survive in databases created before
        // that change — and a settled approval is not shown anywhere in the transcript, so the
        // reads must not surface one whatever is on disk.
        await _fixture.CreateSessionAsync("approval-hidden");
        await _repository.UpsertMessagesAsync("approval-hidden", new[]
        {
            new ChatMessage { Id = 1, Role = ChatRole.User, Content = "send it" },
            new ChatMessage
            {
                Id = 2,
                Role = ChatRole.Event,
                EventKind = "approval",
                EventTitle = "send",
                EventMeta = "Skipped",
                Status = EventStatus.Error,
            },
            new ChatMessage { Id = 3, Role = ChatRole.Agent, Content = "done" },
        });

        var all = await _repository.LoadMessagesAsync("approval-hidden");

        Assert.Equal(new long[] { 1, 3 }, all.Select(m => m.Id));
    }

    private static ChatMessage Message(long id, string content, params ChatAttachment[] attachments)
    {
        var message = new ChatMessage { Id = id, Role = ChatRole.User, Content = content };
        foreach (var attachment in attachments) message.Attachments.Add(attachment);
        return message;
    }

    private static ChatMessage AgentMessage(long id, string content, params ChatAttachment[] attachments)
    {
        var message = Message(id, content, attachments);
        message.Role = ChatRole.Agent;
        return message;
    }

    private static ChatAttachment Attachment(string id) => new()
    {
        Id = id,
        Kind = AttachmentKind.File,
        FileName = id + ".txt",
        MimeType = "text/plain",
        SizeBytes = 10,
        LocalCachePath = "C:\\cache\\" + id + ".txt",
        Status = AttachmentStatus.Sent,
    };

    private static async Task<long> ReadCreatedAtAsync(string conversationId, long messageId)
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT created_at FROM messages WHERE conversation_id = $conversation AND id = $id;";
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$id", messageId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
