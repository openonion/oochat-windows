using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class InteractiveCardPresentationTests
{
    private static ChatMessage FileApproval(string? outcome = null)
    {
        var message = new ChatMessage
        {
            EventKind = "ask_user",
            EventTitle = "Apply changes to /srv/connectonion/examples/test.txt?",
            EventEyebrow = "Question",
            Status = outcome is null ? EventStatus.Running : EventStatus.Done,
            EventMeta = outcome is null ? null : $"Answered: {outcome}",
        };
        message.AskUserOptionEntries.Add(new AskUserOptionEntry { Text = "Yes, apply this change", Owner = message });
        message.AskUserOptionEntries.Add(new AskUserOptionEntry { Text = "Yes to all (auto-approve)", Owner = message });
        message.AskUserOptionEntries.Add(new AskUserOptionEntry { Text = "No, reject and give feedback", Owner = message });
        return message;
    }

    [Fact]
    public void PendingFileApproval_ShowsFullWarningInteraction()
    {
        var message = FileApproval();

        Assert.True(message.ShowAskUserPendingContent);
        Assert.True(message.ShowAskUserStatusBadge);
        Assert.False(message.IsAskUserCompact);
        Assert.Equal(InteractiveVisualTone.Warning, message.AskUserChromeTone);
        Assert.Equal("Approval required", message.AskUserCardTitle);
        Assert.Equal("Apply this change to test.txt?", message.AskUserDisplayQuestion);
        Assert.Equal("Apply once", message.AskUserOptionEntries[0].DisplayText);
    }

    [Theory]
    [InlineData("Yes, apply this change", "Change approved", "Applied once to test.txt", InteractiveVisualTone.Success)]
    [InlineData("Yes to all (auto-approve)", "Similar changes approved", "Auto-approval enabled for this session", InteractiveVisualTone.Success)]
    [InlineData("No, reject and give feedback", "Change rejected", "Feedback requested from the agent", InteractiveVisualTone.Danger)]
    public void ResolvedFileApproval_IsOneCompactResultSummary(
        string answer, string title, string subtitle, InteractiveVisualTone iconTone)
    {
        var message = FileApproval(answer);

        Assert.False(message.ShowAskUserPendingContent);
        Assert.False(message.ShowAskUserStatusBadge);
        Assert.True(message.IsAskUserCompact);
        Assert.Equal(InteractiveVisualTone.Neutral, message.AskUserChromeTone);
        Assert.Equal(iconTone, message.AskUserIconTone);
        Assert.Equal(title, message.AskUserCardTitle);
        Assert.Equal(subtitle, message.AskUserCardSubtitle);
        Assert.DoesNotContain("submitted", $"{title} {subtitle}", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReloadedResolvedCard_DoesNotReturnToPendingPresentation()
    {
        var restored = FileApproval("Yes, apply this change");
        Assert.Equal(InteractiveCardPhase.Pending, restored.InteractivePhase);

        Assert.False(restored.ShowAskUserPendingContent);
        Assert.Equal("Change approved", restored.AskUserCardTitle);
        Assert.Equal("Submitted", restored.InteractiveStateLabel);
    }

    [Fact]
    public void Submitting_KeepsInteractionVisibleButLocksEditing()
    {
        var message = FileApproval();
        message.AskUserOptionEntries[0].Toggle();

        Assert.True(message.TryBeginInteractiveSubmit());

        Assert.True(message.ShowAskUserPendingContent);
        Assert.False(message.IsInteractiveEditable);
        Assert.Equal("Submitting…", message.AskUserSubmitLabel);
    }

    [Fact]
    public void InteractiveTypography_FollowsMessageSizePreference()
    {
        var message = new ChatMessage { MessageFontSize = 18 };

        Assert.Equal(18, message.CardTitleFontSize);
        Assert.Equal(18, message.CardBodyFontSize);
        Assert.Equal(16, message.CardCaptionFontSize);
        Assert.Equal(17, message.CardCodeFontSize);
    }

    [Fact]
    public void LongPlan_UsesBoundedTranscriptPreviewWithoutChangingFullPlan()
    {
        var fullPlan = string.Join('\n', Enumerable.Range(1, 40).Select(index => $"{index}. Step {index}"));
        var message = new ChatMessage { EventDetail = fullPlan };

        Assert.True(message.IsPlanReviewPreviewTruncated);
        Assert.Equal(24, message.PlanReviewPreview.Split('\n').Length);
        Assert.Equal(fullPlan, message.EventDetail);
    }
}
