using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class ChatMessageTests
{
    [Fact]
    public void ConnectionError_UsesMarkerForPresentationButExposesOnlyReadableDetail()
    {
        var error = new ChatMessage
        {
            Role = ChatRole.Agent,
            Content = "[connection error] Unable to connect to the remote server",
        };

        Assert.True(error.IsConnectionError);
        Assert.False(error.IsRegularAgentResponse);
        Assert.Equal("Unable to connect to the remote server", error.ConnectionErrorText);

        error.Content = "The agent replied normally.";

        Assert.False(error.IsConnectionError);
        Assert.True(error.IsRegularAgentResponse);
        Assert.Equal(error.Content, error.ConnectionErrorText);
    }

    [Fact]
    public void MessageActions_RemainDiscoverableAndStrengthenOnHoverOrFocus()
    {
        var message = new ChatMessage
        {
            Role = ChatRole.User,
            Content = "hello",
        };

        Assert.False(message.ShowUserActions);

        // The resting value is the WCAG 3:1 floor for these icon-only controls, not a taste
        // call — see the property's own remarks. Anything below 0.66 fails against the light
        // background, so assert the bound rather than only the current number.
        Assert.Equal(0.75d, message.UserActionsOpacity);
        Assert.True(message.UserActionsOpacity >= 0.66d);

        message.AreUserActionsVisible = true;

        Assert.True(message.ShowUserActions);
        Assert.Equal(1d, message.UserActionsOpacity);

        message.BeginEditing();

        Assert.False(message.ShowUserActions);
        Assert.Equal(0d, message.UserActionsOpacity);
    }

    // The turn-usage row splits its metadata into "model · duration" over the rest. The split is a
    // display concern computed from EventTitle + EventMeta, so pin the segment boundaries down.
    [Fact]
    public void TurnUsage_Split_PutsModelAndFirstSegmentOnLineOne()
    {
        var message = new ChatMessage
        {
            EventTitle = "deepseek-v4-pro",
            EventMeta = "10.5 s · 8K→632 tok · ctx 1.5% · 4 tools",
        };

        Assert.Equal("deepseek-v4-pro · 10.5 s", message.TurnUsagePrimary);
        Assert.Equal("8K→632 tok · ctx 1.5% · 4 tools", message.TurnUsageSecondary);
        Assert.True(message.HasTurnUsageSecondary);
    }

    [Fact]
    public void TurnUsage_Split_SingleSegment_HasNoSecondLine()
    {
        var message = new ChatMessage { EventTitle = "gpt-x", EventMeta = "0.4 s" };

        Assert.Equal("gpt-x · 0.4 s", message.TurnUsagePrimary);
        Assert.Equal("", message.TurnUsageSecondary);
        Assert.False(message.HasTurnUsageSecondary);
    }

    [Fact]
    public void TurnUsage_Split_NoMeta_IsModelOnly()
    {
        var message = new ChatMessage { EventTitle = "gpt-x", EventMeta = null };

        Assert.Equal("gpt-x", message.TurnUsagePrimary);
        Assert.False(message.HasTurnUsageSecondary);
    }

    [Fact]
    public void InteractiveAnswered_ExtractsAnswerForReadOnlyBody()
    {
        var message = new ChatMessage
        {
            Status = EventStatus.Done,
            EventMeta = "Answered: Delete only test.txt (root)",
        };

        Assert.Equal("Delete only test.txt (root)", message.InteractiveAnswerText);
        Assert.True(message.HasInteractiveAnswer);
    }

    [Fact]
    public void InteractivePending_BadgeIsPending_NoAnswerLine()
    {
        var message = new ChatMessage { Status = EventStatus.Running };

        Assert.False(message.HasInteractiveAnswer);
    }

    [Fact]
    public void InteractiveOutcome_WithoutColon_HasNoSeparateAnswer()
    {
        // An approval's "Approved once" carries no free-form answer, so no "Your answer" line.
        var message = new ChatMessage { Status = EventStatus.Done, EventMeta = "Approved once" };

        Assert.False(message.HasInteractiveAnswer);
    }

    // The option cards are hand-drawn Buttons, so exclusivity is the model's job now rather than
    // RadioButton's — which makes it worth pinning down.
    [Fact]
    public void AskUserOption_SingleSelect_KeepsExactlyOneChecked()
    {
        var message = OptionsMessage(multiSelect: false, "A", "B", "C");

        message.AskUserOptionEntries[0].Toggle();
        message.AskUserOptionEntries[2].Toggle();

        Assert.Equal(new[] { false, false, true }, message.AskUserOptionEntries.Select(o => o.IsChecked));
        Assert.Equal("C", message.SelectedOptionText);

        // Clicking the selected option again is a no-op, the way a radio group behaves — it must
        // not leave the turn with nothing chosen.
        message.AskUserOptionEntries[2].Toggle();

        Assert.Equal("C", message.SelectedOptionText);
    }

    [Fact]
    public void AskUserOption_MultiSelect_TogglesIndependently()
    {
        var message = OptionsMessage(multiSelect: true, "A", "B", "C");

        message.AskUserOptionEntries[0].Toggle();
        message.AskUserOptionEntries[1].Toggle();
        message.AskUserOptionEntries[0].Toggle();

        Assert.Equal(new[] { false, true, false }, message.AskUserOptionEntries.Select(o => o.IsChecked));
    }

    [Fact]
    public void AskUserOption_NothingChosen_HasNoSelectedText()
    {
        var message = OptionsMessage(multiSelect: false, "A", "B");

        Assert.Null(message.SelectedOptionText);
    }

    private static ChatMessage OptionsMessage(bool multiSelect, params string[] options)
    {
        var message = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = "ask_user",
            Status = EventStatus.Running,
            AskUserMultiSelect = multiSelect,
        };
        foreach (var option in options)
        {
            message.AskUserOptionEntries.Add(new AskUserOptionEntry { Text = option, Owner = message });
        }
        return message;
    }

    [Fact]
    public void InteractiveOutcome_RunningCard_ReturnsWaitingState()
    {
        var message = InteractiveMessage(EventStatus.Running);

        Assert.Equal("Waiting for you", message.InteractiveOutcome);
    }

    [Theory]
    [InlineData("Approved once")]
    [InlineData("Rejected")]
    [InlineData("Changes requested")]
    public void InteractiveOutcome_ResolvedCard_PreservesAnswer(string outcome)
    {
        var message = InteractiveMessage(EventStatus.Running);
        message.EventMeta = outcome;

        message.Status = EventStatus.Done;

        Assert.Equal(outcome, message.InteractiveOutcome);
        Assert.Contains(outcome, message.InteractiveAccessibilityName);
    }

    [Fact]
    public void InteractiveOutcome_ResolvedWithoutAnswer_ReturnsNoSelection()
    {
        var message = InteractiveMessage(EventStatus.Done);

        Assert.Equal("Skipped", message.InteractiveOutcome);
        Assert.False(message.ShowAskUserFreeText);
    }

    [Fact]
    public void InteractiveAccessibilityName_AnnouncesDisclosureStateAndUpdatesWhenToggled()
    {
        var message = InteractiveMessage(EventStatus.Running);
        var notifications = new List<string?>();
        message.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        Assert.Contains("Expanded", message.InteractiveAccessibilityName, StringComparison.Ordinal);

        message.IsInteractiveCardExpanded = false;

        Assert.Contains("Collapsed", message.InteractiveAccessibilityName, StringComparison.Ordinal);
        Assert.Contains(nameof(ChatMessage.InteractiveAccessibilityName), notifications);
    }

    [Fact]
    public void ThinkingActivity_UsesThoughtProcessTimelinePresentation()
    {
        var message = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = "activity",
            EventTitle = "Thinking",
            EventDetail = "Checking the request before replying.",
            Status = EventStatus.Done,
        };

        Assert.Equal("Thought process", message.ActivityHeaderTitle);
        Assert.Equal("Checking the request before replying.", message.ActivityTimelineText);
        Assert.False(message.IsActivityFailed);

        // The card shows its one entry inline, so the spoken name must carry that text —
        // there is no disclosure state left to announce.
        Assert.Contains("Checking the request before replying.", message.ActivityAccessibilityName);
    }

    [Fact]
    public void ThinkingActivity_WhileRunning_ShowsLoadingHeader()
    {
        var message = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = "activity",
            EventTitle = "Thinking",
            Status = EventStatus.Running,
        };

        Assert.True(message.IsThinkingRunning);
        Assert.Equal("Thinking...", message.ActivityHeaderTitle);

        message.Status = EventStatus.Done;

        Assert.False(message.IsThinkingRunning);
        Assert.Equal("Thought process", message.ActivityHeaderTitle);
    }

    [Fact]
    public void ThinkingActivity_WithTransientStatus_NarratesTheConnectionPhase()
    {
        var message = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = "activity",
            EventTitle = "Thinking",
            TransientStatusText = "Connecting…",
            Status = EventStatus.Running,
        };

        Assert.True(message.IsThinkingRunning);
        Assert.True(message.IsRunningProgressActivity);
        Assert.Equal("Connecting…", message.ActivityHeaderTitle);
        Assert.Equal("Connecting…", message.ActivityAccessibilityName);
    }

    [Fact]
    public void ReconnectActivity_UsesProgressPresentationOnlyWhileRunning()
    {
        var message = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = "activity",
            EventKey = "reconnect",
            EventTitle = "Reconnecting (1/5)",
            Status = EventStatus.Running,
        };

        Assert.True(message.IsRunningProgressActivity);
        Assert.False(message.IsPlainSingleActivity);
        Assert.Equal("Reconnecting (1/5)", message.ActivityHeaderTitle);

        message.Status = EventStatus.Done;

        Assert.False(message.IsRunningProgressActivity);
        Assert.True(message.IsPlainSingleActivity);
    }

    [Fact]
    public void UsageActivity_UsesTurnUsageHeaderAndKeepsMetrics()
    {
        var message = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = "activity",
            EventKey = "turn_usage",
            EventTitle = "model-a",
            EventMeta = "120→30 tok",
            Status = EventStatus.Done,
        };

        Assert.Equal("Turn usage", message.ActivityHeaderTitle);
        Assert.Equal("model-a", message.ActivityTimelineText);
        Assert.True(message.HasActivityTimelineMeta);
        Assert.Equal("model-a · 120→30 tok", message.TurnUsageSummary);
    }

    private static ChatMessage InteractiveMessage(EventStatus status) => new()
    {
        Role = ChatRole.Event,
        EventKind = "approval",
        EventEyebrow = "Approval needed",
        EventTitle = "Run command",
        Status = status,
    };
}
