using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.IntegrationTests.Database;

/// <summary>
/// The resume-on-open probe keys off <c>GetUnfinishedExecutionAsync</c>. It is the only durable
/// evidence that a turn was started and never finished — a turn's bubbles and trace events are
/// written in one batch at the end, so a process killed mid-turn leaves the execution row and
/// the user's message and nothing else.
///
/// <para>Two things about the shared fixture shape these tests. <c>executions.id</c> is the
/// primary key and the whole collection shares one database, so every test needs its own run id
/// or a later insert silently no-ops against an earlier test's row (the repository swallows the
/// constraint violation into a log line). And <c>executions.conversation_id</c> is a foreign key
/// into <c>sessions</c> with no <c>ON DELETE</c>, so rows left behind here make a later test's
/// session delete fail — hence the cleanup.</para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class UnfinishedExecutionTests : IAsyncLifetime
{
    private readonly TempDatabaseFixture _fixture;
    private readonly ConversationRepository _repository = new();
    private readonly List<string> _conversations = new();

    public UnfinishedExecutionTests(TempDatabaseFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var conversationId in _conversations)
        {
            await _repository.DeleteExecutionsAndTracesAsync(conversationId);
            await _repository.DeleteMessagesAsync(conversationId);
        }
    }

    private async Task<string> NewConversationAsync(string id)
    {
        await _fixture.CreateSessionAsync(id);
        _conversations.Add(id);
        return id;
    }

    [Fact]
    public async Task ReturnsNull_WhenConversationHasNoExecutions()
    {
        var conversation = await NewConversationAsync("unfinished-none");
        Assert.Null(await _repository.GetUnfinishedExecutionAsync(conversation));
    }

    [Fact]
    public async Task InsertExecution_MissingConversation_PropagatesForeignKeyFailure()
    {
        var exception = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            _repository.InsertExecutionAsync(
                "missing-execution", "missing-conversation", null, "prompt", "running"));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task FinalizeExecution_MissingExecution_CreatesTerminalLedgerRow()
    {
        var conversation = await NewConversationAsync("unfinished-missing-finalize");

        await _repository.FinalizeExecutionAsync(
            "does-not-exist", conversation, "reply", "done", 100);

        Assert.Null(await _repository.GetUnfinishedExecutionAsync(conversation));
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT result, status, duration_ms FROM executions WHERE id = 'does-not-exist';";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("reply", reader.GetString(0));
        Assert.Equal("done", reader.GetString(1));
        Assert.Equal(100, reader.GetDouble(2));
    }

    [Fact]
    public async Task FinalizeExecution_ExistingIdForAnotherConversation_ReportsTheInvariantViolation()
    {
        var first = await NewConversationAsync("unfinished-finalize-first");
        var second = await NewConversationAsync("unfinished-finalize-second");
        await _repository.InsertExecutionAsync("shared-execution", first, null, "prompt", "running");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.FinalizeExecutionAsync(
                "shared-execution", second, "reply", "done", 100));

        Assert.Contains("does not belong", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindsRunningExecution()
    {
        var conversation = await NewConversationAsync("unfinished-running");
        await _repository.InsertExecutionAsync(
            "unfinished-running-1", conversation, null, "do the thing", "running");

        var unfinished = await _repository.GetUnfinishedExecutionAsync(conversation);

        Assert.NotNull(unfinished);
        Assert.Equal("unfinished-running-1", unfinished!.Value.ExecutionId);
        Assert.Equal("do the thing", unfinished.Value.Prompt);
    }

    /// <summary>A turn that finished normally must never be resumed — that would attach a second
    /// socket to a session the host has already retired.</summary>
    [Fact]
    public async Task IgnoresFinalizedExecution()
    {
        var conversation = await NewConversationAsync("unfinished-done");
        await _repository.InsertExecutionAsync(
            "unfinished-done-1", conversation, null, "prompt", "running");
        await _repository.FinalizeExecutionAsync(
            "unfinished-done-1", conversation, "reply", "done", 1200);

        Assert.Null(await _repository.GetUnfinishedExecutionAsync(conversation));
    }

    /// <summary>Settling an abandoned resume is what stops the probe firing on every open.</summary>
    [Fact]
    public async Task IgnoresExecutionSettledAsAbandoned()
    {
        var conversation = await NewConversationAsync("unfinished-abandoned");
        await _repository.InsertExecutionAsync(
            "unfinished-abandoned-1", conversation, null, "prompt", "running");
        await _repository.FinalizeExecutionAsync(
            "unfinished-abandoned-1", conversation, "", "abandoned", 0);

        Assert.Null(await _repository.GetUnfinishedExecutionAsync(conversation));
    }

    /// <summary>Only one turn can be in flight, so an older unfinished row is debris from an
    /// earlier crash; resuming it would target a long-dead session.</summary>
    [Fact]
    public async Task ReturnsMostRecentWhenSeveralWereLeftRunning()
    {
        var conversation = await NewConversationAsync("unfinished-multi");
        await _repository.InsertExecutionAsync(
            "unfinished-multi-old", conversation, null, "older", "running");
        await Task.Delay(10);
        await _repository.InsertExecutionAsync(
            "unfinished-multi-new", conversation, null, "newer", "running");

        var unfinished = await _repository.GetUnfinishedExecutionAsync(conversation);

        Assert.Equal("unfinished-multi-new", unfinished!.Value.ExecutionId);
    }

    /// <summary>
    /// The alignment guard for recovering a turn the host finished while the app was closed:
    /// the recovery only appends when the transcript still ends at the user message that started
    /// the turn. Unlike <c>LoadLastAgentMessageAsync</c> this must see a trailing user row, which
    /// is exactly the state a crashed turn leaves behind.
    /// </summary>
    [Fact]
    public async Task LoadLastMessage_SeesATrailingUserRow()
    {
        var conversation = await NewConversationAsync("unfinished-last-msg");
        await _repository.UpsertMessagesAsync(conversation, new[]
        {
            new ChatMessage { Id = 1, Role = ChatRole.Agent, Content = "earlier reply" },
            new ChatMessage { Id = 2, Role = ChatRole.User, Content = "the pending prompt" },
        });

        var last = await _repository.LoadLastMessageAsync(conversation);

        Assert.NotNull(last);
        Assert.Equal(ChatRole.User, last!.Role);
        Assert.Equal("the pending prompt", last.Content);
    }

    [Fact]
    public async Task LoadLastMessage_ReturnsNullForAnEmptyConversation()
    {
        var conversation = await NewConversationAsync("unfinished-empty-msgs");
        Assert.Null(await _repository.LoadLastMessageAsync(conversation));
    }

    [Fact]
    public async Task IsScopedToItsConversation()
    {
        var withRun = await NewConversationAsync("unfinished-scope-a");
        var without = await NewConversationAsync("unfinished-scope-b");
        await _repository.InsertExecutionAsync(
            "unfinished-scope-1", withRun, null, "prompt", "running");

        Assert.NotNull(await _repository.GetUnfinishedExecutionAsync(withRun));
        Assert.Null(await _repository.GetUnfinishedExecutionAsync(without));
    }
}
