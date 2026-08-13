using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

/// <summary>
/// The plan card's header has to say which state it is in. It previously passed no tone at all,
/// so an approved plan, a rejected one, and one still blocking the agent rendered identically —
/// Neutral chrome, a grey badge, and a subtitle that still read "The agent will not execute until
/// you respond" long after it had.
/// </summary>
public sealed class PlanReviewCardStateTests
{
    private static ChatMessage PlanCard(EventStatus status, string? meta = null) => new()
    {
        Role = ChatRole.Event,
        EventKind = "plan_review",
        EventTitle = "Review the plan",
        Status = status,
        EventMeta = meta,
    };

    [Theory]
    [InlineData(EventStatus.Running, InteractiveVisualTone.Warning)]
    [InlineData(EventStatus.Error, InteractiveVisualTone.Danger)]
    [InlineData(EventStatus.Done, InteractiveVisualTone.Success)]
    public void IconTone_DistinguishesEveryState(EventStatus status, InteractiveVisualTone expected)
        => Assert.Equal(expected, PlanCard(status).PlanIconTone);

    /// <summary>The rail is a signal, so a settled card draws none — Neutral is what
    /// <c>InteractiveCard.ShowChromeRail</c> reads as "nothing to signal".</summary>
    [Fact]
    public void ChromeRail_OnlyWhileThePlanBlocksTheAgent()
    {
        Assert.Equal(InteractiveVisualTone.Warning, PlanCard(EventStatus.Running).PlanChromeTone);
        Assert.Equal(InteractiveVisualTone.Neutral, PlanCard(EventStatus.Error).PlanChromeTone);
        Assert.Equal(InteractiveVisualTone.Neutral, PlanCard(EventStatus.Done).PlanChromeTone);
    }

    /// <summary>The subtitle is a statement about the present. Once the plan is settled it is
    /// false, so it empties and the row collapses.</summary>
    [Fact]
    public void Subtitle_ClearsOnceTheCardIsSettled()
    {
        Assert.NotEmpty(PlanCard(EventStatus.Running).PlanReviewSubtitle);
        Assert.Empty(PlanCard(EventStatus.Error).PlanReviewSubtitle);
        Assert.Empty(PlanCard(EventStatus.Done).PlanReviewSubtitle);
    }

    /// <summary>The badge and the footer line come from two different properties reading the same
    /// row. Only the label used to check <c>Status</c>, so a rejected plan showed a <b>Rejected</b>
    /// badge directly above "This request has been completed."</summary>
    [Fact]
    public void RejectedCard_DoesNotDescribeItselfAsCompleted()
    {
        var rejected = PlanCard(EventStatus.Error);

        Assert.Equal("Rejected", rejected.InteractiveStateLabel);
        Assert.Equal("This request was rejected.", rejected.InteractiveStateDescription);
    }

    [Fact]
    public void SettledCard_StillReadsAsCompleted()
    {
        var done = PlanCard(EventStatus.Done);

        Assert.Equal("Completed", done.InteractiveStateLabel);
        Assert.Equal("This request has been completed.", done.InteractiveStateDescription);
    }

    /// <summary>An explicit stored marker still wins over the status fallback.</summary>
    [Fact]
    public void StoredMarker_TakesPrecedenceOverTheStatusFallback()
    {
        var skipped = PlanCard(EventStatus.Done, meta: "Skipped");

        Assert.Equal("Skipped", skipped.InteractiveStateLabel);
        Assert.Equal("No response was submitted.", skipped.InteractiveStateDescription);
    }
}
