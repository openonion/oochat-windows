using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

/// <summary>
/// Payloads mirror what <c>session_to_chat_items</c> emits
/// (connectonion/network/host/session/ui.py).
/// </summary>
public class ChatItemsProjectionTests
{
    [Fact]
    public void Parse_MapsUserAndAgentTurns()
    {
        var messages = ChatItemsProjection.Parse(
            """
            [{"id":"msg-0","type":"user","content":"hi"},
             {"id":"msg-1","type":"agent","content":"hello"}]
            """,
            agentName: "Ada");

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal("hi", messages[0].Content);
        Assert.Equal(ChatRole.Agent, messages[1].Role);
        Assert.Equal("hello", messages[1].Content);
        Assert.Equal("Ada", messages[1].AgentName);
    }

    /// <summary>The host's ids are positional (msg-0, thinking-4) and would collide with the
    /// (conversation_id, id) key our repository allocates. Imported rows are renumbered.</summary>
    [Fact]
    public void Parse_AssignsSequentialIdsFromTheGivenStart()
    {
        var messages = ChatItemsProjection.Parse(
            """[{"id":"msg-0","type":"user","content":"a"},{"id":"msg-1","type":"agent","content":"b"}]""",
            agentName: null,
            firstId: 50);

        Assert.Equal(new long[] { 50, 51 }, messages.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void Parse_MapsTraceKindsToCompletedActivityRows()
    {
        var messages = ChatItemsProjection.Parse(
            """
            [{"id":"thinking-1","type":"thinking","content":"pondering"},
             {"id":"intent-2","type":"intent","ack":"understood"},
             {"id":"eval-3","type":"eval","summary":"passed"}]
            """,
            agentName: null);

        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.Equal(ChatRole.Event, m.Role));
        // Imported rows are history by definition — nothing here can still be running.
        Assert.All(messages, m => Assert.Equal(EventStatus.Done, m.Status));
        Assert.Equal("pondering", messages[0].EventDetail);
        Assert.Equal("understood", messages[1].EventDetail);
        Assert.Equal("passed", messages[2].EventDetail);
    }

    /// <summary>An unknown kind is skipped rather than rendered as a blank row.</summary>
    [Fact]
    public void Parse_SkipsUnknownItemTypes()
    {
        var messages = ChatItemsProjection.Parse(
            """[{"id":"x","type":"some_future_kind"},{"id":"msg-0","type":"user","content":"hi"}]""",
            agentName: null);

        Assert.Single(messages);
        Assert.Equal("hi", messages[0].Content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"not\":\"an array\"}")]
    public void Parse_ReturnsEmptyForUnusableInput(string? json)
    {
        // This runs on a connection path; a malformed import must never throw.
        Assert.Empty(ChatItemsProjection.Parse(json, agentName: null));
    }

    // ---- Recovering a turn the host finished while the app was closed ----

    private const string TwoTurns = """
        [{"id":"msg-0","type":"user","content":"first question"},
         {"id":"msg-1","type":"agent","content":"first answer"},
         {"id":"msg-2","type":"user","content":"second question"},
         {"id":"thinking-3","type":"thinking","content":"working"},
         {"id":"msg-4","type":"agent","content":"second answer"}]
        """;

    [Fact]
    public void ParseTailAfterPrompt_ReturnsOnlyWhatFollowedThatTurnsPrompt()
    {
        var tail = ChatItemsProjection.ParseTailAfterPrompt(
            TwoTurns, "second question", agentName: "Ada", firstId: 10);

        Assert.Equal(2, tail.Count);
        Assert.Equal(ChatRole.Event, tail[0].Role);
        Assert.Equal("working", tail[0].EventDetail);
        Assert.Equal("second answer", tail[1].Content);
        Assert.Equal(new long[] { 10, 11 }, tail.Select(m => m.Id).ToArray());
    }

    /// <summary>The same words can legitimately appear earlier in a conversation; the turn being
    /// recovered is always the most recent one, so the anchor must be the last match.</summary>
    [Fact]
    public void ParseTailAfterPrompt_AnchorsOnTheLastMatchingPrompt()
    {
        const string repeated = """
            [{"id":"msg-0","type":"user","content":"again"},
             {"id":"msg-1","type":"agent","content":"early answer"},
             {"id":"msg-2","type":"user","content":"again"},
             {"id":"msg-3","type":"agent","content":"late answer"}]
            """;

        var tail = ChatItemsProjection.ParseTailAfterPrompt(
            repeated, "again", agentName: null, firstId: 1);

        Assert.Equal("late answer", Assert.Single(tail).Content);
    }

    /// <summary>Turns the host ran past the one being recovered must not be imported — their own
    /// user messages are absent locally, so their replies would be answers with no questions.</summary>
    [Fact]
    public void ParseTailAfterPrompt_StopsAtTheNextUserTurn()
    {
        var tail = ChatItemsProjection.ParseTailAfterPrompt(
            TwoTurns, "first question", agentName: null, firstId: 1);

        Assert.Equal("first answer", Assert.Single(tail).Content);
    }

    /// <summary>Refusing to guess is the point: without an anchor an append would splice someone
    /// else's turn into the transcript.</summary>
    [Fact]
    public void ParseTailAfterPrompt_ReturnsEmptyWhenThePromptIsNotFound()
    {
        Assert.Empty(ChatItemsProjection.ParseTailAfterPrompt(
            TwoTurns, "never asked this", agentName: null, firstId: 1));
    }

    [Fact]
    public void ParseTailAfterPrompt_ReturnsEmptyWhenThePromptIsTheLastItem()
    {
        const string unanswered = """
            [{"id":"msg-0","type":"user","content":"pending question"}]
            """;

        Assert.Empty(ChatItemsProjection.ParseTailAfterPrompt(
            unanswered, "pending question", agentName: null, firstId: 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"not\":\"an array\"}")]
    public void ParseTailAfterPrompt_ReturnsEmptyForUnusableInput(string? json)
    {
        Assert.Empty(ChatItemsProjection.ParseTailAfterPrompt(
            json, "anything", agentName: null, firstId: 1));
    }

    [Fact]
    public void ParseTailAfterPrompt_ReturnsEmptyForAnEmptyPrompt()
    {
        // An empty prompt would match nothing meaningful; anchoring on it would be a coin flip.
        Assert.Empty(ChatItemsProjection.ParseTailAfterPrompt(
            TwoTurns, "", agentName: null, firstId: 1));
    }
}
