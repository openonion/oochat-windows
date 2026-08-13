using System.Globalization;
using System.Net.Http;
using ConnectOnion.IntegrationTests.Database;
using ConnectOnion.Protocol;
using ConnectOnion.Protocol.Runtime;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.IntegrationTests.Runtime;

/// <summary>
/// Behavioural cover for the run runtime's persistence half.
///
/// <para>This type owns the connections, the turn lifecycle, and the ordering that lets a page
/// opened mid-turn load history from SQLite and replay live events on top without double-rendering
/// them. Until it moved into <c>Core</c> it sat in the app project, where no headless test host can
/// load it, and its only cover was a source-text scan in <c>StopUiContractTests</c> — a test that
/// greps this file for the string <c>"await connection.SendInterruptAsync()"</c> and so would pass
/// against an implementation that never sent it.</para>
///
/// <para>The socket half (<c>SendMessageAsync</c>, resume probes) still needs a live agent and is
/// covered by <c>ConnectOnion.Protocol.Tests</c>'s fake server against
/// <c>AgentConnectionService</c>. What is reachable here is everything downstream of a snapshot:
/// how a completed turn lands in storage, how a failed one deliberately does not, and how
/// interactive cards are sealed. Those are the paths that decide what a user sees when they reopen
/// a conversation, and they were entirely untested.</para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AgentSessionManagerTests : IAsyncLifetime
{
    private readonly TempDatabaseFixture _fixture;
    private readonly ConversationRepository _repository = new();
    private readonly HttpClient _http = new();
    private readonly List<string> _conversations = new();

    public AgentSessionManagerTests(TempDatabaseFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>The collection shares one database and <c>executions.conversation_id</c> is a
    /// foreign key into <c>sessions</c> with no <c>ON DELETE</c>, so rows left here would make a
    /// later test's session delete fail. Same reasoning as <c>UnfinishedExecutionTests</c>.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var conversationId in _conversations)
        {
            await _repository.DeleteExecutionsAndTracesAsync(conversationId);
            await _repository.DeleteMessagesAsync(conversationId);
        }
        _http.Dispose();
    }

    // ---- Completed turns ----

    [Fact]
    public async Task PersistCompletedAsync_WritesTurnBubbles_AndMarksHistoryPersisted()
    {
        var manager = NewManager();
        var conversationId = await NewConversationAsync("persist-completed");
        var snapshot = Snapshot(conversationId, ConversationRunStatus.Completed, "run-completed",
            Event("assistant", """{"content":"the reply"}"""));

        await manager.PersistCompletedAsync(snapshot, "the reply");

        var messages = await _repository.LoadMessagesAsync(conversationId);
        Assert.Contains(messages, m => m.Content == "the reply");

        // The marker is what tells a page subscribing mid-turn that storage is authoritative and
        // it must not also replay this run's events.
        Assert.True(manager.IsRunHistoryPersisted("run-completed"));
    }

    [Fact]
    public async Task PersistCompletedAsync_SettlesTheExecutionRow()
    {
        var manager = NewManager();
        var conversationId = await NewConversationAsync("persist-settles");
        await _repository.InsertExecutionAsync("run-settles", conversationId, null, "user asked", "running");

        await manager.PersistCompletedAsync(
            Snapshot(conversationId, ConversationRunStatus.Completed, "run-settles"), "done");

        // A settled row is what stops the next open from probing the host to rejoin this turn.
        Assert.Null(await _repository.GetUnfinishedExecutionAsync(conversationId));
    }

    [Fact]
    public async Task PersistCompletedAsync_WritesTraceEvents()
    {
        var manager = NewManager();
        var conversationId = await NewConversationAsync("persist-trace");

        await manager.PersistCompletedAsync(
            Snapshot(conversationId, ConversationRunStatus.Completed, "run-trace",
                Event("tool_call", """{"tool_id":"1","name":"bash","args":{}}"""),
                Event("assistant", """{"content":"ok"}""")),
            "ok");

        Assert.True(await TraceEventCountAsync(conversationId) >= 2);
    }

    // ---- Failed turns ----

    [Fact]
    public async Task PersistFailedAsync_ShutdownInterrupted_LeavesExecutionRunningForResume()
    {
        var manager = NewManager();
        var conversationId = await NewConversationAsync("persist-shutdown");
        await _repository.InsertExecutionAsync("run-shutdown", conversationId, null, "user asked", "running");

        await manager.PersistFailedAsync(Snapshot(
            conversationId, ConversationRunStatus.Failed, "run-shutdown", errorCode: RunErrorCodes.Shutdown));

        // Deliberately unsettled: the host was never told to stop, so its agent is probably still
        // running this turn and the row is the only thing that makes the next open rejoin it.
        var unfinished = await _repository.GetUnfinishedExecutionAsync(conversationId);
        Assert.NotNull(unfinished);
    }

    [Fact]
    public async Task PersistFailedAsync_ShutdownInterrupted_WritesNoBubbles()
    {
        var manager = NewManager();
        var conversationId = await NewConversationAsync("persist-shutdown-empty");

        await manager.PersistFailedAsync(Snapshot(
            conversationId, ConversationRunStatus.Failed, "run-shutdown-empty",
            errorCode: RunErrorCodes.Shutdown,
            events: Event("assistant", """{"content":"partial output"}""")));

        // Writing the partial turn here would double-render it: a resumed turn replays from the
        // start of the host's buffer, so this bubble would be projected a second time.
        var messages = await _repository.LoadMessagesAsync(conversationId);
        Assert.DoesNotContain(messages, m => m.Content == "partial output");
    }

    [Theory]
    [InlineData(nameof(SessionNotRunningException), true)]
    [InlineData(RunErrorCodes.Shutdown, false)]
    [InlineData("SomeOtherFailure", false)]
    [InlineData(null, false)]
    public void IsAbandonedResume_OnlyMatchesTheResumeProbesOwnErrorCode(string? errorCode, bool expected)
    {
        var snapshot = Snapshot("c", ConversationRunStatus.Failed, "r", errorCode: errorCode);
        Assert.Equal(expected, AgentSessionManager.IsAbandonedResume(snapshot));
    }

    [Theory]
    [InlineData(RunErrorCodes.Shutdown, true)]
    [InlineData(nameof(SessionNotRunningException), false)]
    [InlineData(null, false)]
    public void IsShutdownInterrupted_OnlyMatchesTheShutdownCode(string? errorCode, bool expected)
    {
        var snapshot = Snapshot("c", ConversationRunStatus.Failed, "r", errorCode: errorCode);
        Assert.Equal(expected, AgentSessionManager.IsShutdownInterrupted(snapshot));
    }

    // ---- Interactive card sealing ----
    //
    // The event stream carries no reply, so what the user chose reaches storage only through
    // RecordInteractiveAnswer. Anything a terminal run left unanswered has to be sealed, or a
    // reloaded conversation shows dead controls that can never be answered.

    [Fact]
    public async Task PersistCompletedAsync_UnansweredAskUser_IsSealedAsSkipped()
    {
        var manager = NewManager();
        var conversationId = await NewConversationAsync("seal-ask");

        await manager.PersistCompletedAsync(
            Snapshot(conversationId, ConversationRunStatus.Completed, "run-seal-ask",
                AskUser("q1", "Which file?")),
            "done");

        var card = await LoadEventCardAsync(conversationId, "ask_user");
        Assert.Equal("Skipped", card.EventMeta);
        Assert.Equal(EventStatus.Done, card.Status);
    }

    [Fact]
    public async Task PersistCompletedAsync_AnsweredAskUser_KeepsTheUsersAnswer()
    {
        var manager = NewManager();
        var conversationId = await NewConversationAsync("seal-answered");
        manager.RecordInteractiveAnswer(conversationId, "Answered: main.cs", EventStatus.Done);

        await manager.PersistCompletedAsync(
            Snapshot(conversationId, ConversationRunStatus.Completed, "run-seal-answered",
                AskUser("q1", "Which file?")),
            "done");

        var card = await LoadEventCardAsync(conversationId, "ask_user");
        Assert.Equal("Answered: main.cs", card.EventMeta);
        Assert.Equal(EventStatus.Done, card.Status);
    }

    [Fact]
    public async Task PersistCompletedAsync_RecordedAnswers_AreStampedInCreationOrder()
    {
        var manager = NewManager();
        var conversationId = await NewConversationAsync("seal-order");
        manager.RecordInteractiveAnswer(conversationId, "Answered: first", EventStatus.Done);
        manager.RecordInteractiveAnswer(conversationId, "Answered: second", EventStatus.Done);

        await manager.PersistCompletedAsync(
            Snapshot(conversationId, ConversationRunStatus.Completed, "run-seal-order",
                AskUser("q1", "First question?"),
                AskUser("q2", "Second question?")),
            "done");

        var cards = (await _repository.LoadMessagesAsync(conversationId))
            .Where(m => m.EventKind == "ask_user")
            .OrderBy(m => m.Id)
            .ToList();

        // FIFO against creation order is the whole contract: mis-align it and every answer lands
        // on the wrong question.
        Assert.Equal(2, cards.Count);
        Assert.Equal("Answered: first", cards[0].EventMeta);
        Assert.Equal("Answered: second", cards[1].EventMeta);
    }

    [Fact]
    public async Task PersistCompletedAsync_CancelledReservation_DoesNotConsumeALaterAnswer()
    {
        var manager = NewManager();
        var conversationId = await NewConversationAsync("seal-cancelled");

        // A reply whose socket write failed. It must not be stamped, and — the sharper half — it
        // must not consume the queue slot belonging to the answer that did land.
        var abandoned = manager.BeginInteractiveAnswer(conversationId, "Answered: never sent", EventStatus.Done);
        manager.CancelInteractiveAnswer(abandoned);
        manager.RecordInteractiveAnswer(conversationId, "Answered: really sent", EventStatus.Done);

        await manager.PersistCompletedAsync(
            Snapshot(conversationId, ConversationRunStatus.Completed, "run-seal-cancelled",
                AskUser("q1", "Which file?")),
            "done");

        var card = await LoadEventCardAsync(conversationId, "ask_user");
        Assert.Equal("Answered: really sent", card.EventMeta);
    }

    [Fact]
    public async Task PersistCompletedAsync_ConfirmedReservation_IsStamped()
    {
        var manager = NewManager();
        var conversationId = await NewConversationAsync("seal-confirmed");

        var reservation = manager.BeginInteractiveAnswer(conversationId, "Answered: confirmed", EventStatus.Done);
        manager.ConfirmInteractiveAnswer(reservation);

        await manager.PersistCompletedAsync(
            Snapshot(conversationId, ConversationRunStatus.Completed, "run-seal-confirmed",
                AskUser("q1", "Which file?")),
            "done");

        var card = await LoadEventCardAsync(conversationId, "ask_user");
        Assert.Equal("Answered: confirmed", card.EventMeta);
    }

    [Fact]
    public async Task PersistCompletedAsync_ApprovalCards_LeaveTheTranscript()
    {
        var manager = NewManager();
        var conversationId = await NewConversationAsync("seal-approval");

        await manager.PersistCompletedAsync(
            Snapshot(conversationId, ConversationRunStatus.Completed, "run-seal-approval",
                Event("approval_needed", """{"tool":"bash","reason":"runs a command","arguments":{"command":"ls"}}""")),
            "done");

        // Asserted against the raw table, NOT through LoadMessagesAsync. The repository filters
        // `event_kind IS NOT 'approval'` on every read as defence in depth for rows written by
        // older builds, so reading through it makes this assertion unfalsifiable — it holds even
        // if the persist path writes the row. The contract under test is that the row never
        // reaches storage in the first place.
        Assert.Equal(0, await RawEventKindCountAsync(conversationId, "approval"));

        // Guard against the inverse mistake: a snapshot that produced no approval card at all
        // would also satisfy the assertion above. The tool-activity card is the approval's sole
        // UI, so its presence is what proves the event was really projected.
        Assert.True(await RawEventKindCountAsync(conversationId, "tool_activity") > 0);
    }

    // ---- Lifecycle ----

    [Fact]
    public async Task RequestStopAsync_NoActiveRun_ReportsNotRunning()
    {
        var manager = NewManager();
        Assert.Equal(StopOutcome.NotRunning, await manager.RequestStopAsync("never-ran"));
    }

    [Fact]
    public async Task SetModeAsync_NoConnection_IsANoOp()
    {
        var manager = NewManager();
        await manager.SetModeAsync("never-connected", AgentModes.Safe);
    }

    [Fact]
    public async Task ReleaseConversationAsync_DropsPendingInteractiveAnswers()
    {
        var manager = NewManager();
        var conversationId = await NewConversationAsync("release");
        manager.RecordInteractiveAnswer(conversationId, "Answered: stale", EventStatus.Done);

        await manager.ReleaseConversationAsync(conversationId);

        // A deleted conversation must leave nothing behind that a later run could pick up: the
        // stale answer must not be stamped onto a card in the next turn.
        await manager.PersistCompletedAsync(
            Snapshot(conversationId, ConversationRunStatus.Completed, "run-release",
                AskUser("q1", "Which file?")),
            "done");

        var card = await LoadEventCardAsync(conversationId, "ask_user");
        Assert.Equal("Skipped", card.EventMeta);
    }

    [Fact]
    public async Task GetActiveRun_NeverRanConversation_IsNull()
    {
        var manager = NewManager();
        Assert.Null(manager.GetActiveRun("never-ran"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ShutdownAsync_WithNoRuns_Completes()
    {
        var manager = NewManager();
        await manager.ShutdownAsync();
    }

    // ---- Helpers ----

    private AgentSessionManager NewManager() => new(_http, _repository);

    private async Task<string> NewConversationAsync(string id)
    {
        await _fixture.CreateSessionAsync(id);
        _conversations.Add(id);
        return id;
    }

    private async Task<ChatMessage> LoadEventCardAsync(string conversationId, string eventKind)
    {
        var messages = await _repository.LoadMessagesAsync(conversationId);
        return Assert.Single(messages, m => m.EventKind == eventKind);
    }

    /// <summary>Counts rows straight out of <c>messages</c>, bypassing the repository's read-side
    /// approval filter. Any assertion about what the persist path writes has to go through here.</summary>
    private static async Task<int> RawEventKindCountAsync(string conversationId, string eventKind)
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM messages WHERE conversation_id = $id AND event_kind = $kind;";
        command.Parameters.AddWithValue("$id", conversationId);
        command.Parameters.AddWithValue("$kind", eventKind);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<int> TraceEventCountAsync(string conversationId)
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM trace_events WHERE session_id = $id;";
        command.Parameters.AddWithValue("$id", conversationId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static AgentStreamEvent Event(string type, string json) => new(type, type, null, json);

    private static AgentStreamEvent AskUser(string id, string text) =>
        Event("ask_user", $$"""{"id":"{{id}}","text":"{{text}}","options":["yes","no"]}""");

    private static ConversationRunSnapshot Snapshot(
        string conversationId,
        ConversationRunStatus status,
        string runId,
        params AgentStreamEvent[] events) =>
        Snapshot(conversationId, status, runId, errorCode: null, events);

    private static ConversationRunSnapshot Snapshot(
        string conversationId,
        ConversationRunStatus status,
        string runId,
        string? errorCode,
        params AgentStreamEvent[] events) =>
        new(
            RunId: runId,
            ConversationId: conversationId,
            AgentId: "agent-1",
            UserMessageId: "user-1",
            AssistantMessageId: "assistant-1",
            Status: status,
            PartialContent: "",
            Sequence: 1,
            ErrorCode: errorCode,
            ErrorMessage: null,
            Events: events);
}
