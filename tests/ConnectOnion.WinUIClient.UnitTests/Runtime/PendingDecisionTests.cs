using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

/// <summary>
/// The rule behind the composer's pending-decision bar and the go-to-decision shortcut.
/// <para>Nothing in the client used to know a card was blocking, which left two independent ways
/// to answer one question — the card's Submit, and the composer, whose typing routes to
/// <c>SendRuntimeInputAsync</c> mid-turn and does not resolve the card.</para>
/// </summary>
public class PendingDecisionTests
{
    [Fact]
    public void NoInteractiveCards_NothingIsPending()
    {
        Assert.Null(PendingDecision.Find([User("hi"), Agent("hello")]));
        Assert.Null(PendingDecision.Find([]));
    }

    [Fact]
    public void RunningInteractiveCard_IsPending()
    {
        var ask = Interactive("ask_user", EventStatus.Running);

        Assert.Same(ask, PendingDecision.Find([User("hi"), ask]));
    }

    [Fact]
    public void AnsweredCard_IsNotPending()
    {
        var ask = Interactive("ask_user", EventStatus.Running);
        ask.CompleteInteractiveSubmit("Answered: staging");

        Assert.Null(PendingDecision.Find([ask]));
    }

    /// <summary>Approvals and unanswered file-change questions are rendered by another card and
    /// have no transcript row of their own, but they are what the turn is parked on — and their
    /// position is precisely what is hard to find, since an approval is drawn inside the turn's one
    /// tool-activity card, anchored wherever the turn's first tool call landed.</summary>
    [Theory]
    [InlineData("approval")]
    [InlineData("plan_review")]
    public void CardsWithoutTheirOwnRow_StillCount(string kind)
    {
        var card = Interactive(kind, EventStatus.Running);
        Assert.Same(card, PendingDecision.Find([card]));
    }

    [Fact]
    public void HiddenFileChangeApproval_StillCounts()
    {
        var diff = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = "diff_preview",
            EventTitle = "/tmp/test.txt",
            EventDetail = "+new",
            Status = EventStatus.Done,
        };
        var ask = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = "ask_user",
            EventTitle = "Apply changes to /tmp/test.txt?",
            Status = EventStatus.Running,
        };
        ask.AttachRelatedDiffPreview(diff);

        Assert.False(ask.IsTranscriptRowVisible);
        Assert.Same(ask, PendingDecision.Find([diff, ask]));
    }

    /// <summary>Interactive turns are sequential, so the blocking one is the most recent. A
    /// forward scan would return a card an earlier, abnormally-ended turn left Running — pointing
    /// the user at a decision they can no longer make while the real one waits below it.</summary>
    [Fact]
    public void StaleRunningCardFromAnEarlierTurn_LosesToTheLatestOne()
    {
        var abandoned = Interactive("ask_user", EventStatus.Running);
        var current = Interactive("plan_review", EventStatus.Running);

        Assert.Same(current, PendingDecision.Find([abandoned, Agent("..."), current]));
    }

    [Fact]
    public void SettlingTheLastCard_FallsBackToNothingRatherThanAnOlderOne()
    {
        var abandoned = Interactive("ask_user", EventStatus.Running);
        var current = Interactive("plan_review", EventStatus.Running);
        current.CompleteInteractiveSubmit("Plan approved");

        // The older card is still Running, but it is above a settled one, so the scan reaches it.
        // That is the honest answer: it really is unanswered, and the bar pointing at it is better
        // than silently dropping a card the turn never sealed.
        Assert.Same(abandoned, PendingDecision.Find([abandoned, current]));
    }

    private static ChatMessage Interactive(string kind, EventStatus status) => new()
    {
        Role = ChatRole.Event,
        EventKind = kind,
        EventTitle = "Which environment?",
        Status = status,
    };

    private static ChatMessage User(string text) => new() { Role = ChatRole.User, Content = text };
    private static ChatMessage Agent(string text) => new() { Role = ChatRole.Agent, Content = text };
}
