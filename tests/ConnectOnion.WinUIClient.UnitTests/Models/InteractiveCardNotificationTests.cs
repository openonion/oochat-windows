using System.ComponentModel;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

/// <summary>
/// That a settled interactive card actually repaints.
///
/// <para>Every visual on these cards is a computed property over <c>Status</c>, so answering one
/// changes nothing on screen unless the property is named in
/// <c>NotifyInteractivePresentationChanged</c>. That list is hand-maintained, and the plan card's
/// four tone properties were never added to it — an approved or rejected plan kept the amber
/// "waiting" rail, the amber clock glyph and a subtitle still claiming the agent was blocked, until
/// a container recycle or a reload happened to re-read them.</para>
/// </summary>
public class InteractiveCardNotificationTests
{
    [Fact]
    public void ApprovedPlan_RepaintsItsChrome()
    {
        var card = PlanReview();
        Assert.Equal(InteractiveVisualTone.Warning, card.PlanChromeTone);
        Assert.Equal(InteractiveVisualTone.Warning, card.PlanIconTone);
        Assert.NotEmpty(card.PlanReviewSubtitle);

        var raised = Record(card, () => card.CompleteInteractiveSubmit("Plan approved"));

        Assert.Equal(InteractiveVisualTone.Neutral, card.PlanChromeTone);
        Assert.Equal(InteractiveVisualTone.Success, card.PlanIconTone);
        // The subtitle is a claim about the present; a settled card must stop making it.
        Assert.Empty(card.PlanReviewSubtitle);

        Assert.Contains(nameof(ChatMessage.PlanChromeTone), raised);
        Assert.Contains(nameof(ChatMessage.PlanIconTone), raised);
        Assert.Contains(nameof(ChatMessage.PlanIconGlyph), raised);
        Assert.Contains(nameof(ChatMessage.PlanReviewSubtitle), raised);
    }

    [Fact]
    public void RejectedPlan_RepaintsItsChrome()
    {
        var card = PlanReview();

        var raised = Record(card, () => card.CompleteInteractiveSubmit("Rejected", rejected: true));

        Assert.Equal(InteractiveVisualTone.Danger, card.PlanIconTone);
        Assert.Equal(InteractiveVisualTone.Neutral, card.PlanChromeTone);
        Assert.Contains(nameof(ChatMessage.PlanIconTone), raised);
        Assert.Contains(nameof(ChatMessage.PlanChromeTone), raised);
    }

    [Fact]
    public void AnsweredAskUser_RepaintsItsChrome()
    {
        var card = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = "ask_user",
            EventTitle = "Which environment?",
            Status = EventStatus.Running,
        };

        var raised = Record(card, () => card.CompleteInteractiveSubmit("Answered: staging"));

        Assert.Equal(InteractiveVisualTone.Neutral, card.AskUserChromeTone);
        Assert.Equal(InteractiveVisualTone.Success, card.AskUserIconTone);
        Assert.Contains(nameof(ChatMessage.AskUserChromeTone), raised);
        Assert.Contains(nameof(ChatMessage.AskUserIconTone), raised);
        Assert.Contains(nameof(ChatMessage.AskUserIconGlyph), raised);
    }

    /// <summary>The guard against the next omission of this kind.
    ///
    /// <para>Enumerates every public property whose value actually differs before and after a card
    /// settles, and requires each to have been announced. A new computed property that reads
    /// <c>Status</c> and is not added to the funnel fails here rather than shipping as a card that
    /// silently keeps its old colours.</para></summary>
    [Theory]
    [InlineData("plan_review")]
    [InlineData("ask_user")]
    public void EveryVisualThatChangesOnSettling_IsAnnounced(string kind)
    {
        var card = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = kind,
            EventTitle = "Review the plan",
            EventDetail = "1. Inspect",
            Status = EventStatus.Running,
        };

        var readable = typeof(ChatMessage).GetProperties()
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToList();

        var before = Snapshot(card, readable);
        var raised = Record(card, () => card.CompleteInteractiveSubmit("Plan approved"));
        var after = Snapshot(card, readable);

        var changedButSilent = before
            .Where(entry => !Equals(entry.Value, after[entry.Key]))
            .Select(entry => entry.Key)
            .Where(name => !raised.Contains(name))
            .ToList();

        Assert.True(
            changedButSilent.Count == 0,
            $"These {kind} properties changed when the card settled but were never raised, so the "
            + $"card would keep rendering its old value: {string.Join(", ", changedButSilent)}");
    }

    private static Dictionary<string, object?> Snapshot(
        ChatMessage card, IEnumerable<System.Reflection.PropertyInfo> properties)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            // A property that throws is not a rendering concern; skip rather than fail the sweep.
            try { values[property.Name] = property.GetValue(card); }
            catch { }
        }
        return values;
    }

    private static HashSet<string> Record(ChatMessage card, Action act)
    {
        var raised = new HashSet<string>(StringComparer.Ordinal);
        void Handler(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is { } name) raised.Add(name);
        }

        card.PropertyChanged += Handler;
        try { act(); }
        finally { card.PropertyChanged -= Handler; }
        return raised;
    }

    private static ChatMessage PlanReview() => new()
    {
        Role = ChatRole.Event,
        EventKind = "plan_review",
        EventEyebrow = "Plan review",
        EventTitle = "Review the plan",
        EventDetail = "1. Inspect\n2. Change",
        Status = EventStatus.Running,
    };
}
