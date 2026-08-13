using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

public sealed class ChatTurnProjectionTests
{
    [Fact]
    public void AppendFinalReply_MatchesLastAssistantBubble_DoesNotAddDuplicate()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);
        projection.Apply(Event("assistant", "{\"content\":\"same reply\"}"));

        projection.AppendFinalReply("same reply");

        Assert.Single(target.Messages);
        Assert.Equal("same reply", target.Messages[0].Content);
    }

    [Fact]
    public void AppendFinalReply_DiffersFromLastAssistantBubble_AddsReply()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);
        projection.Apply(Event("assistant", "{\"content\":\"streamed partial\"}"));

        projection.AppendFinalReply("final reply");

        Assert.Equal(2, target.Messages.Count);
        Assert.Equal("final reply", target.Messages[1].Content);
    }

    [Fact]
    public void AppendCompletedTurn_PutsUsageBeforeStreamedFinalReply()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);
        projection.Apply(Event("llm_result", "{\"model\":\"model-a\",\"usage\":{\"input_tokens\":120,\"output_tokens\":30},\"duration_ms\":250}"));
        projection.Apply(Event("assistant", "{\"content\":\"final reply\"}"));

        projection.AppendCompletedTurn("final reply");

        // The summary is process metadata, so it sits with the process records; the turn ends on
        // the answer.
        Assert.Equal(2, target.Messages.Count);
        Assert.True(target.Messages[0].IsEvent);
        Assert.Equal("turn_usage", target.Messages[0].EventKey);
        Assert.Equal("model-a", target.Messages[0].EventTitle);
        Assert.Contains("120→30 tok", target.Messages[0].EventMeta);
        Assert.True(target.Messages[1].IsAgent);
        Assert.Equal("final reply", target.Messages[1].Content);
    }

    /// <summary>
    /// Persisted history is replayed with <c>ORDER BY id</c>, so list order and id order have to
    /// agree — otherwise reopening a conversation shows the summary and the answer swapped
    /// relative to the live view that produced them. The streamed bubble is created mid-turn and
    /// therefore has to be renumbered when it is moved below the summary.
    /// </summary>
    [Fact]
    public void AppendCompletedTurn_StreamedFinalReply_IsRenumberedToMatchItsNewPosition()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);
        projection.Apply(Event("thinking", "{\"content\":\"working\"}"));
        projection.Apply(Event("llm_result", "{\"model\":\"model-a\",\"duration_ms\":250}"));
        projection.Apply(Event("assistant", "{\"content\":\"final reply\"}"));

        projection.AppendCompletedTurn("final reply");

        var ids = target.Messages.Select(message => message.Id).ToList();
        Assert.Equal(ids.OrderBy(id => id), ids);
        Assert.Equal("final reply", target.Messages[^1].Content);
    }

    [Fact]
    public void AppendCompletedTurn_WithoutReportedUsage_StillAddsHonestSummary()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.AppendCompletedTurn("final reply");

        Assert.Equal(2, target.Messages.Count);
        Assert.Equal("Turn usage", target.Messages[0].EventTitle);
        Assert.Equal("Usage not reported", target.Messages[0].EventMeta);
        Assert.Equal("final reply", target.Messages[1].Content);
    }

    [Fact]
    public void Apply_Thinking_StaysLoadingUntilReplyArrives()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("thinking", "{\"content\":\"Checking the request.\"}"));

        var thinking = Assert.Single(target.Messages);
        Assert.True(thinking.IsThinkingRunning);
        Assert.Equal("Thinking...", thinking.ActivityHeaderTitle);
        Assert.Equal("Checking the request.", thinking.ActivityTimelineText);

        projection.Apply(Event("assistant", "{\"content\":\"Final reply\"}"));

        Assert.False(thinking.IsThinkingRunning);
        Assert.Equal(EventStatus.Done, thinking.Status);
        Assert.Equal("Thought process", thinking.ActivityHeaderTitle);
        Assert.False(thinking.HasMultipleThoughts);
    }

    [Fact]
    public void ApplyOptimisticStopVisuals_EndsRunningThinkingAndToolAnimations()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);
        projection.Apply(Event("thinking", "{\"content\":\"Checking the request.\"}"));
        projection.Apply(Event(
            "tool_call",
            "{\"tool_id\":\"1\",\"name\":\"search_web\",\"args\":{\"query\":\"test\"}}"));

        ChatTurnProjection.ApplyOptimisticStopVisuals(target.Messages);

        var thinking = Assert.Single(target.Messages, message => message.IsThinkingEvent);
        Assert.Equal(EventStatus.Done, thinking.Status);
        Assert.False(thinking.IsThinkingRunning);

        var toolCard = Assert.Single(target.Messages, message => message.IsToolActivityEvent);
        Assert.True(toolCard.ToolActivity!.IsTerminal);
        Assert.Equal(ToolActivityStatus.Success, toolCard.ToolActivity.Status);
        Assert.All(toolCard.ToolActivity.Steps, step => Assert.Equal(ToolStepStatus.Success, step.Status));
    }

    [Fact]
    public void Apply_ConsecutiveThinking_GroupsIntoOneFoldableCard()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("thinking", "{\"content\":\"First, check the address.\"}"));
        projection.Apply(Event("thinking", "{\"content\":\"Then draft the subject.\"}"));
        projection.Apply(Event("thinking", "{\"content\":\"Then send it.\"}"));

        var thinking = Assert.Single(target.Messages);
        Assert.True(thinking.HasMultipleThoughts);
        Assert.False(thinking.IsSingleActivityEntry);
        Assert.Equal(3, thinking.ThoughtSteps.Count);
        Assert.Equal("Thinking...", thinking.ActivityHeaderTitle);
        Assert.Equal("3 steps", thinking.ThoughtStepsSummary);
    }

    [Fact]
    public void Apply_ThinkingSeparatedOnlyByInvisibleLifecycleFrames_StaysOneCard()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("thinking", "{\"content\":\"The first attempt used the wrong type.\"}"));
        projection.Apply(Event("llm_result", "{\"duration_ms\":8000}"));
        projection.Apply(Event("llm_call", "{\"model\":\"test-model\",\"iteration\":2}"));
        projection.Apply(Event("thinking", "{\"content\":\"Retry with an integer.\"}"));

        var thinking = Assert.Single(target.Messages);
        Assert.Equal(2, thinking.ThoughtSteps.Count);
        Assert.Equal(
            ["The first attempt used the wrong type.", "Retry with an integer."],
            thinking.ThoughtSteps);
    }

    [Fact]
    public void Apply_InvisibleUpdatesToExistingToolCard_DoNotSplitAdjacentThinking()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("tool_call", "{\"tool_id\":\"one\",\"name\":\"dashboard\"}"));
        projection.Apply(Event("thinking", "{\"content\":\"The dashboard call failed.\"}"));
        projection.Apply(Event("tool_result", "{\"tool_id\":\"one\",\"status\":\"error\",\"error\":\"type mismatch\"}"));
        projection.Apply(Event("llm_call", "{\"model\":\"test-model\",\"iteration\":2}"));
        projection.Apply(Event("thinking", "{\"content\":\"Retry with the corrected value.\"}"));

        var thinking = Assert.Single(target.Messages, message => message.IsThinkingEvent);
        Assert.Equal(2, thinking.ThoughtSteps.Count);
    }

    [Fact]
    public void Apply_ThinkingSeparatedByAToolCall_StaysTwoCards()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("thinking", "{\"content\":\"Look up the recipient.\"}"));
        projection.Apply(Event("tool_call", "{\"id\":\"t1\",\"name\":\"lookup\"}"));
        projection.Apply(Event("thinking", "{\"content\":\"Now compose the mail.\"}"));

        // Thoughts either side of a tool call are reasoning about different things — merging them
        // would claim the agent planned it all before calling anything.
        var thoughts = target.Messages.Where(m => m.IsThinkingEvent).ToList();
        Assert.Equal(2, thoughts.Count);
        Assert.All(thoughts, t => Assert.Single(t.ThoughtSteps));
    }

    [Fact]
    public void Apply_ReplayedAskUser_DoesNotRaiseASecondCard()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);
        const string frame = "{\"id\":\"q1\",\"text\":\"Pick one\",\"options\":[\"A\",\"B\"]}";

        projection.Apply(Event("ask_user", frame));
        // What a reconnect does: the host replays frames the client never acknowledged.
        projection.Apply(Event("ask_user", frame));

        var card = Assert.Single(target.Messages, m => m.EventKind == "ask_user");
        Assert.Equal(2, card.AskUserOptionEntries.Count);
    }

    [Fact]
    public void Apply_ReplayedCompletedAskUser_DoesNotBecomePendingAgain()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);
        const string frame = "{\"id\":\"q1\",\"text\":\"Pick one\",\"options\":[\"A\",\"B\"]}";

        projection.Apply(Event("ask_user", frame));
        var card = Assert.Single(target.Messages);
        card.CompleteInteractiveSubmit("A");
        projection.Apply(Event("ask_user", frame));

        Assert.Single(target.Messages);
        Assert.Equal(EventStatus.Done, card.Status);
        Assert.False(card.IsInteractiveEditable);
    }

    [Fact]
    public void Apply_ReplayedPlanReview_ReusesCardAndReconnectRestoresOnlyPendingCard()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);
        const string frame = "{\"plan_content\":\"1. Inspect\"}";

        projection.Apply(Event("plan_review", frame));
        var card = Assert.Single(target.Messages);
        card.MarkInteractiveConnectionLost();
        projection.Apply(Event("plan_review", frame));

        Assert.Single(target.Messages);
        Assert.Equal("Waiting", card.InteractiveStateLabel);
        Assert.True(card.IsInteractiveEditable);

        card.CompleteInteractiveSubmit("Plan approved");
        projection.Apply(Event("plan_review", frame));
        Assert.Single(target.Messages);
        Assert.Equal(EventStatus.Done, card.Status);
        Assert.False(card.IsInteractiveEditable);
        Assert.False(card.IsInteractiveCardExpanded);
    }

    /// <summary>An answered ask_user folds like every other settled card — and, unlike before, can
    /// be opened again.
    ///
    /// <para>It used to report "no body" once answered (<c>ShowBody</c> was the same flag as "still
    /// waiting"), and <c>InteractiveCard.RefreshDisclosure</c> derives <c>CanCollapse</c> from
    /// that — so the chevron disappeared, the header left the tab order, and the question the user
    /// had answered became unreachable, in history too.</para>
    ///
    /// <para>Re-opening costs nothing extra: the three input blocks are each gated on
    /// <c>IsAwaitingResponse</c>, so a settled card shows the question and its attachments and
    /// never replays a password box.</para></summary>
    [Fact]
    public void AnsweredAskUser_FoldsButKeepsItsQuestionReachable()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("ask_user",
            "{\"id\":\"ask-1\",\"question\":\"Which environment?\",\"options\":[\"staging\",\"prod\"]}"));
        var card = Assert.Single(target.Messages);
        Assert.True(card.IsInteractiveCardExpanded);
        Assert.True(card.ShowAskUserBody);
        Assert.True(card.ShowAskUserOptions);

        card.CompleteInteractiveSubmit("Answered: staging");

        Assert.False(card.IsInteractiveCardExpanded);
        // The body survives, so the disclosure control does too.
        Assert.True(card.ShowAskUserBody);
        Assert.Equal("Which environment?", card.AskUserDisplayQuestion);
        // But the inputs do not come back.
        Assert.False(card.ShowAskUserOptions);
        Assert.False(card.ShowAskUserFields);
        Assert.False(card.ShowAskUserFreeText);
    }

    [Fact]
    public void Apply_LlmCall_OpensThinkingIndicator_AndLlmResultTakesItAway()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("llm_call", "{\"model\":\"deepseek-v4-pro\",\"iteration\":1}"));

        var thinking = Assert.Single(target.Messages);
        Assert.True(thinking.IsThinkingRunning);

        // No reasoning text on an llm_call, so the row must not print the internal "Thinking"
        // marker as though the agent had said it — the spinner and the live counter are the row.
        Assert.False(thinking.HasThoughtText);
        Assert.Equal("", thinking.ActivityTimelineText);

        projection.Apply(Event("llm_result", "{\"model\":\"deepseek-v4-pro\",\"duration_ms\":2000}"));

        // The wait is over, so the row goes. No "thought for" epitaph is left behind: the turn's
        // total time is on the usage line.
        Assert.Empty(target.Messages);
    }

    [Fact]
    public void Apply_ManyLlmIterations_NeverStackIndicatorRows()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        // An agentic turn: think, call a tool, think again, and again.
        projection.Apply(Event("llm_call", "{\"model\":\"m\",\"iteration\":1}"));
        projection.Apply(Event("llm_result", "{\"duration_ms\":2400}"));
        projection.Apply(Event("tool_call", "{\"tool_id\":\"t1\",\"name\":\"read_file\"}"));
        projection.Apply(Event("llm_call", "{\"model\":\"m\",\"iteration\":2}"));
        projection.Apply(Event("llm_result", "{\"duration_ms\":6600}"));
        projection.Apply(Event("llm_call", "{\"model\":\"m\",\"iteration\":3}"));

        // Eight round trips used to leave eight spent rows. At most one is ever on screen, and
        // only while a call is actually out.
        var thinking = Assert.Single(target.Messages, m => m.IsThinkingEvent);
        Assert.True(thinking.IsThinkingRunning);

        projection.Apply(Event("llm_result", "{\"duration_ms\":5100}"));

        Assert.DoesNotContain(target.Messages, m => m.IsThinkingEvent);
    }

    [Fact]
    public void Apply_LlmCallWithoutResult_RemovesTheIndicatorAtTurnEnd()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("llm_call", "{\"model\":\"m\",\"iteration\":1}"));
        projection.CompleteToolActivity(ToolActivityStatus.Failed, "boom");

        // A run that dies mid-call never gets llm_result; the spinner must not turn forever.
        Assert.DoesNotContain(target.Messages, m => m.IsThinkingEvent);
    }

    [Fact]
    public void Apply_ThinkingText_SuppressesTheBareLlmIndicator()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("thinking", "{\"content\":\"Checking the request.\"}"));
        projection.Apply(Event("llm_call", "{\"model\":\"m\",\"iteration\":1}"));

        // A host that sends both must not stack an empty spinner on top of the real thought.
        var thinking = Assert.Single(target.Messages);
        Assert.Equal("Checking the request.", thinking.ActivityTimelineText);
    }

    [Fact]
    public void CompleteToolActivity_Failed_StopsThinkingLoader()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);
        projection.Apply(Event("thinking", "{\"content\":\"Checking the request.\"}"));

        projection.CompleteToolActivity(ToolActivityStatus.Failed, "failed");

        var thinking = Assert.Single(target.Messages);
        Assert.False(thinking.IsThinkingRunning);
        Assert.Equal(EventStatus.Error, thinking.Status);
    }

    [Fact]
    public void AppendCompletedTurn_AfterInterrupt_MarksFinalOutputAsStopped()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);
        projection.Apply(Event("interrupt_requested", "{\"type\":\"interrupt_requested\"}"));
        projection.Apply(Event("assistant", "{\"content\":\"What would you like me to do?\"}"));

        projection.AppendCompletedTurn("What would you like me to do?");

        var reply = Assert.Single(target.Messages, message => message.IsAgent);
        Assert.Equal("Stopped", reply.EventMeta);
    }

    [Fact]
    public void AppendCompletedTurn_AfterInterruptSendFailure_DoesNotMarkOutputStopped()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);
        projection.Apply(Event("interrupt_requested", "{\"type\":\"interrupt_requested\"}"));
        projection.Apply(Event("interrupt_request_failed", "{\"type\":\"interrupt_request_failed\"}"));

        projection.AppendCompletedTurn("normal reply");

        var reply = Assert.Single(target.Messages, message => message.IsAgent);
        Assert.Null(reply.EventMeta);
    }

    [Fact]
    public void Apply_ConsecutiveToolCalls_UsesSingleActivityBubbleWithMultipleSteps()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("tool_call", "{\"tool_id\":\"one\",\"name\":\"read_file\",\"args\":{\"path\":\"a.txt\"}}"));
        projection.Apply(Event("tool_call", "{\"tool_id\":\"two\",\"name\":\"search_web\",\"args\":{\"query\":\"test\"}}"));

        var bubble = Assert.Single(target.Messages);
        Assert.Equal("tool_activity", bubble.EventKind);
        Assert.Equal(2, bubble.ToolActivity!.Steps.Count);
    }

    [Fact]
    public void AppendCompletedTurn_ToolCallsSeparatedByOtherEvents_StayInOneActivityCard()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("tool_call", "{\"tool_id\":\"one\",\"name\":\"read_file\",\"args\":{\"path\":\"a.txt\"}}"));
        projection.Apply(Event("tool_result", "{\"tool_id\":\"one\",\"status\":\"success\",\"result\":\"ok\"}"));
        projection.Apply(Event("thinking", "{\"content\":\"Checking another file\"}"));
        projection.Apply(Event("llm_call", "{\"model\":\"test-model\"}"));
        projection.Apply(Event("llm_result", "{\"duration_ms\":10}"));
        projection.Apply(Event("tool_call", "{\"tool_id\":\"two\",\"name\":\"read_file\",\"args\":{\"path\":\"b.txt\"}}"));
        projection.Apply(Event("tool_result", "{\"tool_id\":\"two\",\"status\":\"success\",\"result\":\"ok\"}"));

        projection.AppendCompletedTurn("Done");

        var card = Assert.Single(target.Messages, message => message.IsToolActivityEvent);
        Assert.Equal(2, card.ToolActivity!.Steps.Count);
        Assert.Equal(ToolActivityStatus.Success, card.ToolActivity.Status);
    }

    [Fact]
    public void Apply_OnboardRequiredRepeated_AddsOnlyOnePendingBubble()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("ONBOARD_REQUIRED", "{}"));
        projection.Apply(Event("ONBOARD_REQUIRED", "{}"));

        Assert.Single(target.Messages);
        Assert.True(target.Messages[0].IsOnboarding);
    }

    [Fact]
    public void Apply_OnboardRequired_OffersWhicheverMethodsTheGateAccepts()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("ONBOARD_REQUIRED",
            """{"methods":["invite_code","payment"],"payment_amount":5,"payment_address":"0xabc"}"""));

        var card = target.Messages[0];
        Assert.True(card.ShowOnboardInviteCode);
        Assert.True(card.ShowOnboardPayment);
        Assert.True(card.ShowOnboardMethodDivider);
        Assert.Equal("0xabc", card.OnboardPaymentAddress);
        Assert.Contains("invite code or a payment", card.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_OnboardRequired_PaymentOnlyGate_HidesTheInviteCodeBox()
    {
        // Before the gate's methods were read, this card demanded an invite code that does not
        // exist — the agent was simply unreachable.
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("ONBOARD_REQUIRED", """{"methods":["payment"],"payment_amount":5}"""));

        var card = target.Messages[0];
        Assert.False(card.ShowOnboardInviteCode);
        Assert.True(card.ShowOnboardPayment);
        Assert.False(card.ShowOnboardMethodDivider);
        // Nothing invented a destination for the transfer.
        Assert.False(card.HasOnboardPaymentAddress);
    }

    [Fact]
    public void Apply_OnboardRequired_WithNoMethods_StillOffersTheInviteCode()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("ONBOARD_REQUIRED", "{}"));

        var card = target.Messages[0];
        Assert.True(card.ShowOnboardInviteCode);
        Assert.False(card.ShowOnboardPayment);
    }

    [Fact]
    public void Apply_OnboardRequired_SubmittingCollapsesEveryMethodRow()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);
        projection.Apply(Event("ONBOARD_REQUIRED",
            """{"methods":["invite_code","payment"],"payment_amount":5}"""));

        target.Messages[0].IsOnboardingSubmitted = true;

        // A spent gate must not go on offering a second answer.
        Assert.False(target.Messages[0].ShowOnboardInviteCode);
        Assert.False(target.Messages[0].ShowOnboardPayment);
    }

    [Fact]
    public void Apply_OnboardSuccess_RetiresThePendingInviteCard()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("ONBOARD_REQUIRED", "{}"));
        projection.Apply(Event("ONBOARD_SUCCESS", "{}"));

        var card = target.Messages[0];
        Assert.True(card.IsOnboardingSubmitted);
        Assert.False(card.IsOnboarding);
        Assert.False(card.IsOnboardingInputEnabled);
        Assert.Equal("Onboarding complete", target.Messages[1].EventTitle);
    }

    [Fact]
    public void Apply_ModeChangedByAgent_AddsCardSayingWhoChangedIt()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("mode_changed", "{\"mode\":\"plan\",\"triggered_by\":\"agent\"}"));

        var bubble = Assert.Single(target.Messages);
        Assert.Equal("activity", bubble.EventKind);
        Assert.Equal("Mode: Plan", bubble.EventTitle);
        // The agent can enter plan mode on its own, and that has to be visible: it changes what the
        // agent is allowed to do for the rest of the turn.
        Assert.Equal("by agent", bubble.EventMeta);
    }

    [Fact]
    public void Apply_SessionSync_IsStateOnlyAndDoesNotRenderTechnicalBubble()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("session_sync", """
            {"type":"session_sync","session":{"session_id":"abc","mode":"plan","turn":3}}
            """));

        Assert.Empty(target.Messages);
    }

    [Fact]
    public void AgentImage_IsHeldUntilTurnCompletes_ThenAttachesToFinalReply()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        // The image arrives mid-turn, before the assistant reply and the usage summary.
        projection.Apply(Event("agent_image", "{\"type\":\"agent_image\",\"image\":\"data:image/png;base64,AAAA\"}"));

        // Nothing rendered yet: it must wait for the agent's final output.
        Assert.Empty(target.Messages);
        Assert.Empty(target.ResolvedImages);

        projection.Apply(Event("assistant", "{\"content\":\"here you go\"}"));
        projection.AppendCompletedTurn("here you go");

        // Order: the quiet usage summary, then the final reply carrying the image.
        Assert.Equal(2, target.Messages.Count);
        Assert.True(target.Messages[0].IsEvent);
        var reply = target.Messages[1];
        Assert.True(reply.IsAgent);
        Assert.Equal("here you go", reply.Content);
        Assert.Single(reply.Attachments);
        Assert.Equal(AttachmentKind.Image, reply.Attachments[0].Kind);
        Assert.Single(target.ResolvedImages);
    }

    [Fact]
    public void AgentImage_WithNoTextReply_GetsItsOwnBubbleAfterTheSummary()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("agent_image", "{\"type\":\"agent_image\",\"image\":\"data:image/png;base64,AAAA\"}"));
        projection.AppendCompletedTurn("");

        // With no text reply the image bubble *is* the output, so it still ends the turn — the
        // summary does not become the last thing on screen.
        Assert.Equal(2, target.Messages.Count);
        Assert.True(target.Messages[0].IsEvent);
        Assert.True(target.Messages[1].IsAgent);
        Assert.Single(target.Messages[1].Attachments);

        var ids = target.Messages.Select(message => message.Id).ToList();
        Assert.Equal(ids.OrderBy(id => id), ids);
    }

    [Fact]
    public void AgentImage_PreparedCachePath_DoesNotDecodeAgainOnEitherProjectionPass()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("agent_image",
            "{\"type\":\"agent_image\",\"cached_path\":\"images/hash.png\",\"mime_type\":\"image/png\"}"));
        projection.AppendCompletedTurn("");

        var attachment = Assert.Single(target.Messages[^1].Attachments);
        Assert.Equal("images/hash.png", attachment.LocalCachePath);
        Assert.Equal("image/png", attachment.MimeType);
        Assert.Equal(AttachmentStatus.Sent, attachment.Status);
        Assert.Empty(target.ResolvedImages);
    }

    [Fact]
    public void AgentImage_ImmediatelyBeforeAskUser_AttachesWhileQuestionIsPending()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("agent_image",
            "{\"type\":\"agent_image\",\"image\":\"data:image/png;base64,AAAA\"}"));
        projection.Apply(Event("ask_user",
            "{\"id\":\"login\",\"text\":\"Scan this QR code\",\"options\":[\"Done\"]}"));

        var question = Assert.Single(target.Messages);
        Assert.Equal("ask_user", question.EventKind);
        Assert.Equal(EventStatus.Running, question.Status);
        Assert.Single(question.Attachments);
        Assert.Single(target.ResolvedImages);

        projection.AppendCompletedTurn("");
        Assert.Single(question.Attachments);
    }

    // ---- Approval embedded in the tool-activity card ----

    [Fact]
    public void ApprovalAfterAToolCall_EmbedsInTheSameCard_WithoutAnExtraStep()
    {
        var target = new FakeTarget { IsLiveView = true };
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("tool_call", "{\"tool_id\":\"one\",\"name\":\"remote_write_file\",\"args\":{\"path\":\"/notes/plan.txt\"}}"));
        projection.Apply(Event("approval_needed",
            "{\"tool\":\"Remote Write File\",\"description\":\"writes a file\",\"arguments\":{\"path\":\"/notes/plan.txt\"}}"));

        // One tool-activity card, one step — the approval added no second timeline row.
        var toolCard = target.Messages.Single(m => m.IsToolActivityEvent);
        Assert.Single(toolCard.ToolActivity!.Steps);
        Assert.Equal(ToolActivityStatus.WaitingForConfirmation, toolCard.ToolActivity.Status);

        // The approval is linked into that card and marked embedded (so it draws no standalone row).
        var approval = target.Messages.Single(m => m.IsApprovalEvent);
        Assert.True(approval.IsEmbeddedApproval);
        Assert.Same(approval, toolCard.ToolActivity.Approval);
        Assert.True(toolCard.ToolActivity.HasApproval);
    }

    [Fact]
    public void ApprovalAsTheFirstEvent_StillCreatesTheToolActivityCardToHostIt()
    {
        var target = new FakeTarget { IsLiveView = true };
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("approval_needed",
            "{\"tool\":\"Remote Write File\",\"arguments\":{\"path\":\"/notes/plan.txt\"}}"));

        // The tool-activity card exists (so the approval renders inside it) even with no prior
        // tool_call, and carries no fabricated step.
        var toolCard = Assert.Single(target.Messages, m => m.IsToolActivityEvent);
        Assert.Empty(toolCard.ToolActivity!.Steps);
        Assert.Same(target.Messages.Single(m => m.IsApprovalEvent), toolCard.ToolActivity.Approval);
    }

    [Fact]
    public void Approval_ExtractsTargetAndBuildsPrompt_FromLiveData()
    {
        var target = new FakeTarget { IsLiveView = true };
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("approval_needed",
            "{\"tool\":\"Remote Write File\",\"arguments\":{\"path\":\"/home/me/Project_Almond_weekly_meeting_notes.txt\"}}"));

        var approval = target.Messages.Single(m => m.IsApprovalEvent);
        Assert.Equal("Approve file operation?", approval.ApprovalPromptTitle);
        Assert.Equal("Remote Write File", approval.OwnerToolActivity!.HeaderTitle);
        Assert.Equal("This tool wants to modify:", approval.ApprovalOperationLine);
        Assert.Equal("Project_Almond_weekly_meeting_notes.txt", approval.ApprovalTargetText);
        Assert.Equal(ApprovalTargetKind.File, approval.ApprovalTargetKind);
        Assert.False(approval.ShowApprovalFallback);
    }

    [Fact]
    public void Approval_UsesSecurityReasonAndShowsRemainingBatchTools()
    {
        var target = new FakeTarget { IsLiveView = true };
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("approval_needed", """
            {"tool":"remote_bash","description":"Replace a value",
             "reason":"Blocked: sed can modify server state.","arguments":{"command":"sed -i ..."},
             "batch_remaining":[{"tool":"remote_edit_file","arguments":{"file_path":"/etc/config.yaml"}}]}
            """));

        var approval = target.Messages.Single(m => m.IsApprovalEvent);
        Assert.Equal("Blocked: sed can modify server state.", approval.EventDetail);
        Assert.Equal("This batch also includes 1: remote_edit_file", approval.ApprovalBatchSummary);
        Assert.True(approval.HasApprovalBatch);
    }

    [Fact]
    public void Approval_WithNoExtractableTarget_FallsBackToTheGenericPrompt()
    {
        var target = new FakeTarget { IsLiveView = true };
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("approval_needed",
            "{\"tool\":\"Do Thing\",\"arguments\":{\"reason\":\"because\"}}"));

        var approval = target.Messages.Single(m => m.IsApprovalEvent);
        Assert.False(approval.HasApprovalTarget);
        Assert.True(approval.ShowApprovalFallback);
    }

    [Fact]
    public void RepeatedApprovalFrame_OnAReconnect_DoesNotDuplicateTheStandaloneRow()
    {
        // The projection dedupes ask_user by id; approval has no id, but the host only re-parks the
        // single tool card. Two frames must not leave two visible approvals — they collapse onto the
        // one card's Approval slot (the later frame wins), so the card never shows a doubled decision.
        var target = new FakeTarget { IsLiveView = true };
        var projection = new ChatTurnProjection(target);
        var frame = "{\"tool\":\"Remote Write File\",\"arguments\":{\"path\":\"/notes/plan.txt\"}}";

        projection.Apply(Event("approval_needed", frame));
        projection.Apply(Event("approval_needed", frame));

        var toolCard = Assert.Single(target.Messages, m => m.IsToolActivityEvent);
        // There is exactly one hidden backing message, so persistence answer alignment cannot be
        // shifted by a replayed frame.
        var approval = Assert.Single(target.Messages, m => m.IsApprovalEvent);
        Assert.True(approval.IsEmbeddedApproval);
        Assert.Same(approval, toolCard.ToolActivity!.Approval);
    }

    [Fact]
    public void SequentialApprovals_ShareOneAggregatedCard_SlotHoldsTheCurrentOne()
    {
        var target = new FakeTarget { IsLiveView = true };
        var projection = new ChatTurnProjection(target);

        // Tool A needs approval, then (after it runs) tool B needs approval. Approvals are
        // sequential, so one aggregated card holds both tools as steps and the single approval slot
        // holds whichever decision is currently pending.
        projection.Apply(Event("tool_call", "{\"tool_id\":\"a\",\"name\":\"remote_write_file\",\"args\":{\"path\":\"a.txt\"}}"));
        projection.Apply(Event("approval_needed", "{\"tool\":\"Write A\",\"arguments\":{\"path\":\"a.txt\"}}"));
        projection.Apply(Event("tool_result", "{\"tool_id\":\"a\",\"status\":\"success\",\"result\":\"ok\"}"));
        projection.Apply(Event("tool_call", "{\"tool_id\":\"b\",\"name\":\"remote_write_file\",\"args\":{\"path\":\"b.txt\"}}"));
        projection.Apply(Event("approval_needed", "{\"tool\":\"Write B\",\"arguments\":{\"path\":\"b.txt\"}}"));

        var card = Assert.Single(target.Messages, m => m.IsToolActivityEvent);
        Assert.Equal(2, card.ToolActivity!.Steps.Count);
        Assert.Equal("Write B", card.ToolActivity.Approval!.EventTitle);  // current pending decision
    }

    [Fact]
    public void CompletedTurn_SealsAStillPendingApproval_SoTheCardDoesNotShowWaiting()
    {
        // The screenshot bug: a finished turn whose embedded approval was resolved elsewhere (a
        // separate ask_user, or the turn was stopped) must not keep advertising "Waiting for
        // approval" on a card that plainly says "Tool execution completed".
        var target = new FakeTarget { IsLiveView = true };
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("tool_call", "{\"tool_id\":\"a\",\"name\":\"delete_file\",\"args\":{\"path\":\"test.txt\"}}"));
        projection.Apply(Event("approval_needed", "{\"tool\":\"Delete File\",\"arguments\":{\"path\":\"test.txt\"}}"));
        var card = Assert.Single(target.Messages, m => m.IsToolActivityEvent);
        Assert.True(card.ToolActivity!.IsAwaitingApproval);   // pending while running

        // The turn finishes without the embedded approval being answered through the card.
        projection.AppendCompletedTurn("Done! The file test.txt has been deleted.");

        Assert.False(card.ToolActivity.IsAwaitingApproval);   // no stale "Waiting for approval"
        Assert.Null(card.ToolActivity.Approval);
    }

    [Fact]
    public void Apply_ModeChangedToPlan_SaysWhatPlanModeMeansForTheUser()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("mode_changed", """{"mode":"plan","triggered_by":"agent"}"""));

        var row = target.Messages[0];
        Assert.Equal("by agent", row.EventMeta);
        // Someone who has never seen plan mode has no way to know a review is coming.
        Assert.Contains("plan for approval", row.EventDetail, StringComparison.Ordinal);
        // Stated about the protocol, not as the agent speaking — the agent said nothing of the kind.
        Assert.StartsWith("The agent will", row.EventDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_ModeChangedToAnythingElse_AddsNoExplanation()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("mode_changed", """{"mode":"safe"}"""));

        Assert.Null(target.Messages[0].EventDetail);
    }

    // ---- Live usage readout ------------------------------------------------------------------

    [Fact]
    public void Apply_LlmResult_PublishesTheTurnsRunningTotal()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("llm_result",
            """{"usage":{"input_tokens":1000,"output_tokens":50},"context_percent":12.5,"tool_calls_count":1}"""));

        var usage = Assert.Single(target.UsageReports);
        Assert.Equal(1000, usage.TokensIn);
        Assert.Equal(50, usage.TokensOut);
        Assert.Equal(12.5, usage.ContextPercent);
        Assert.Equal(1, usage.ToolCalls);
    }

    [Fact]
    public void Apply_SeveralLlmResults_ReportsTheRunningTotalNotThePerIterationFigure()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("llm_result", """{"usage":{"input_tokens":100,"output_tokens":10}}"""));
        projection.Apply(Event("llm_result", """{"usage":{"input_tokens":200,"output_tokens":20}}"""));

        // "This turn has cost me X so far" is the question; a per-iteration figure would flicker
        // and answer one nobody asks.
        Assert.Equal(2, target.UsageReports.Count);
        Assert.Equal(300, target.UsageReports[^1].TokensIn);
        Assert.Equal(30, target.UsageReports[^1].TokensOut);
    }

    [Fact]
    public void Apply_OnlyLlmResultPublishes_SoAnUnchangedValueIsNotRepublished()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("llm_call", "{}"));
        projection.Apply(Event("thinking", """{"content":"hmm"}"""));
        projection.Apply(Event("tool_call", """{"tool_id":"1","name":"bash","args":{}}"""));

        Assert.Empty(target.UsageReports);
    }

    [Fact]
    public void CurrentUsage_WithNothingReported_IsEmptySoTheReadoutStaysHidden()
    {
        var target = new FakeTarget();
        var projection = new ChatTurnProjection(target);

        // An agent that says nothing must leave a clean composer, not a row of zeroes: unreported
        // is not the same as zero.
        Assert.True(projection.CurrentUsage.IsEmpty);

        projection.Apply(Event("llm_result", "{}"));
        Assert.True(target.UsageReports[^1].IsEmpty);
    }

    private static AgentStreamEvent Event(string type, string json) => new(type, type, null, json);

    private sealed class FakeTarget : IChatProjectionTarget
    {
        private long _nextId = 1;
        public IList<ChatMessage> Messages { get; } = new List<ChatMessage>();
        public List<string> ResolvedImages { get; } = new();
        public string? AgentName => "Test Agent";
        public bool IsLiveView { get; set; }
        public long NextId() => _nextId++;
        public void Add(ChatMessage message) => Messages.Add(message);
        public void ResolveAgentImage(string dataUrl, ChatAttachment attachment)
        {
            ResolvedImages.Add(dataUrl);
            attachment.Status = AttachmentStatus.Sent;
        }

        /// <summary>Every report the projection published, so a test can assert what a live
        /// composer would have shown at each point rather than only the end state.</summary>
        public List<TurnUsage> UsageReports { get; } = new();
        public void ReportUsage(TurnUsage usage) => UsageReports.Add(usage);
    }
}
