using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

public sealed class ToolActivityExpansionTests
{
    [Theory]
    [InlineData(ToolActivityStatus.Failed)]
    [InlineData(ToolActivityStatus.Cancelled)]
    [InlineData(ToolActivityStatus.PartialSuccess)]
    [InlineData(ToolActivityStatus.Success)]
    [InlineData(ToolActivityStatus.Running)]
    [InlineData(ToolActivityStatus.WaitingForConfirmation)]
    [InlineData(ToolActivityStatus.WaitingForPermission)]
    public void ToggleExpanded_DrivesTimelineVisibility_ForEveryStatus(ToolActivityStatus status)
    {
        var activity = new ToolActivityViewModel { Status = status, IsExpanded = true };

        activity.ToggleExpanded();
        Assert.False(activity.IsTimelineVisible);

        activity.ToggleExpanded();
        Assert.True(activity.IsTimelineVisible);
    }

    /// <summary>The header chevron is one rotated ChevronDown, matching the sidebar's agent rows
    /// (ShellAgentItem.ChevronAngle) — down when open, pointing right when closed. Pinned because
    /// the two are meant to stay the same gesture, and nothing but a test connects them.</summary>
    [Fact]
    public void ChevronAngle_MatchesTheSidebarsDownRightConvention()
    {
        var activity = new ToolActivityViewModel();
        var changed = new List<string>();
        activity.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        Assert.False(activity.IsExpanded);
        Assert.Equal(270, activity.ChevronAngle);   // closed → points right

        activity.ToggleExpanded();

        Assert.Equal(0, activity.ChevronAngle);     // open → points down
        // The view binds the angle, so it only turns if the change is announced.
        Assert.Contains(nameof(ToolActivityViewModel.ChevronAngle), changed);
    }

    /// <summary>
    /// The header's duration is the span the *tools* covered, taken from their frames' own
    /// timestamps — not how long the projection object has been alive.
    ///
    /// <para>This is the persist path in miniature: a card built now, replaying a turn that ran
    /// earlier. Timing it by the card's own lifetime reported every reopened conversation as
    /// "· 2 ms", because building and completing it happen microseconds apart.</para>
    /// </summary>
    [Fact]
    public void HeaderDuration_ComesFromTheStepTimestamps_NotTheCardsLifetime()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);

        // ts values 7.2s apart, exactly as a replayed turn's frames carry them.
        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"remote_write_file\",\"ts\":1784467095.5}"));
        projector.ApplyResult(Result("{\"tool_id\":\"one\",\"result\":\"ok\",\"ts\":1784467102.7}"));

        projector.Complete();

        Assert.Equal(7.2, activity.DurationSeconds, precision: 1);
        Assert.Equal("7.2 s", activity.DurationLabel);
        Assert.Contains("7.2 s", activity.Summary);
    }

    /// <summary>Several steps: the span runs from the earliest call to the latest result, not
    /// just the last step's own duration.</summary>
    [Fact]
    public void HeaderDuration_SpansEveryStep()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);

        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"read_file\",\"ts\":1784467000}"));
        projector.ApplyResult(Result("{\"tool_id\":\"one\",\"result\":\"ok\",\"ts\":1784467002}"));
        projector.ApplyCall(Event("{\"tool_id\":\"two\",\"name\":\"send_email\",\"ts\":1784467003}"));
        projector.ApplyResult(Result("{\"tool_id\":\"two\",\"result\":\"sent\",\"ts\":1784467010}"));

        projector.Complete();

        Assert.Equal(10, activity.DurationSeconds, precision: 1);
    }

    /// <summary>A host whose clock disagrees with ours can stamp a result before its call. The
    /// span clamps to zero rather than running backwards.</summary>
    [Fact]
    public void HeaderDuration_NeverRunsBackwards()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);

        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"read_file\",\"ts\":1784467100}"));
        projector.ApplyResult(Result("{\"tool_id\":\"one\",\"result\":\"ok\",\"ts\":1784467090}"));

        projector.Complete();

        Assert.Equal(0, activity.DurationSeconds);
    }

    [Fact]
    public void NewActivity_StartsCollapsed()
    {
        var activity = new ToolActivityViewModel();

        Assert.False(activity.IsExpanded);
        Assert.False(activity.IsTimelineVisible);
    }

    /// <summary>The headline behaviour: a running turn shows its timeline, a finished one folds
    /// it away so the agent's actual reply is not pushed down the page.</summary>
    [Fact]
    public void RunningTools_OpenTheCard_AndCompletionFoldsItAgain()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity, isLiveView: true);

        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"read_file\",\"args\":{\"path\":\"a.txt\"}}"));
        Assert.True(activity.IsExpanded);

        projector.Complete();
        Assert.False(activity.IsExpanded);
    }

    /// <summary>
    /// The guard that makes expand-while-running liveable. Folding a noisy card mid-turn has to
    /// stick — without the override, the next tool_call throws it back open and the card fights
    /// the user once per tool.
    /// </summary>
    [Fact]
    public void UserCollapseMidRun_IsNotUndoneByTheNextTool()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity, isLiveView: true);

        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"read_file\",\"args\":{}}"));
        Assert.True(activity.IsExpanded);

        activity.ToggleExpanded();
        Assert.False(activity.IsExpanded);

        projector.ApplyCall(Event("{\"tool_id\":\"two\",\"name\":\"search_web\",\"args\":{\"query\":\"x\"}}"));

        Assert.False(activity.IsExpanded);
        Assert.Equal(2, activity.Steps.Count);   // still recorded, just not shown
    }

    /// <summary>The reverse override: a user who opens a card keeps it open past completion.</summary>
    [Fact]
    public void UserExpandedCard_StaysOpenAfterCompletion()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity, isLiveView: true);
        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"read_file\",\"args\":{}}"));

        activity.ToggleExpanded();   // closed
        activity.ToggleExpanded();   // and deliberately reopened

        projector.Complete();

        Assert.True(activity.IsExpanded);
    }

    /// <summary>Persisted history must never be written expanded, or reopening an old
    /// conversation replays every past run at full height.</summary>
    [Fact]
    public void RunningTools_StayCollapsedOnTheHeadlessPass()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);   // isLiveView: false

        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"read_file\",\"args\":{}}"));

        Assert.False(activity.IsExpanded);
    }

    [Fact]
    public void SuccessfulRun_StaysCollapsedOnCompletion()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"read_file\",\"args\":{}}"));

        projector.Complete();

        Assert.False(activity.IsExpanded);
    }

    [Fact]
    public void BlockedTool_OpensTheCard_OnlyOnALiveView()
    {
        var live = new ToolActivityViewModel();
        new ToolActivityProjector(live, isLiveView: true)
            .AddBlocked(Event("{\"tool\":\"run_command\",\"message\":\"blocked by policy\"}"));
        Assert.True(live.IsExpanded);

        // The persistence pass writes the same card collapsed — reopening the conversation later
        // must not replay the failure at full height.
        var persisted = new ToolActivityViewModel();
        new ToolActivityProjector(persisted)
            .AddBlocked(Event("{\"tool\":\"run_command\",\"message\":\"blocked by policy\"}"));
        Assert.False(persisted.IsExpanded);
    }

    [Fact]
    public void WaitingOnTheUser_FoldsTheCard()
    {
        // This used to open the card, on the reasoning that what the tool was about to do is what
        // the decision hinges on. The decision controls sit at the *bottom* of the card though,
        // below every step the turn has already run — so opening it pushed the one actionable part
        // off the viewport behind its own history, and grew the card by hundreds of pixels at the
        // moment an approval arrived. The command and its risk are in the approval section, which
        // is a sibling of the timeline and stays either way.
        var live = new ToolActivityViewModel();
        new ToolActivityProjector(live, isLiveView: true).WaitForConfirmation();
        Assert.False(live.IsExpanded);

        var persisted = new ToolActivityViewModel();
        new ToolActivityProjector(persisted).WaitForConfirmation();
        Assert.False(persisted.IsExpanded);
    }

    [Fact]
    public void AnsweredApproval_StaysFoldedOnASuccessfulCompletion()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity, isLiveView: true);
        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"send_email\",\"args\":{}}"));
        projector.WaitForConfirmation();
        Assert.False(activity.IsExpanded);

        projector.Complete();

        // The decision is made and the tool succeeded; the card has nothing left to demand.
        Assert.False(activity.IsExpanded);
    }

    /// <summary>A turn that is still working reopens once the decision is out of the way — folding
    /// is tied to the wait, not to having had an approval at some point.</summary>
    [Fact]
    public void AfterTheDecision_AResumedTurnReopensTheTimeline()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity, isLiveView: true);
        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"send_email\",\"args\":{}}"));
        projector.WaitForConfirmation();
        Assert.False(activity.IsExpanded);

        projector.ApplyCall(Event("{\"tool_id\":\"two\",\"name\":\"send_email\",\"args\":{}}"));

        Assert.True(activity.IsExpanded);
    }

    [Fact]
    public void DetailedDisplayMode_StillExpands()
    {
        var activity = new ToolActivityViewModel { DisplayMode = ToolDisplayMode.Detailed };

        Assert.True(activity.IsExpanded);
    }

    [Fact]
    public void CollapsedErrorLine_TakesTheLastFailedStep_AndHidesWhenExpanded()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        // Two steps, so the card keeps its chrome — the collapsed line exists to rescue a folded
        // card, and a single-step card is never folded (see the test below).
        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"read_file\",\"args\":{}}"));
        projector.ApplyResult(Result("{\"tool_id\":\"one\",\"status\":\"success\",\"result\":\"ok\"}"));
        projector.ApplyCall(Event("{\"tool_id\":\"two\",\"name\":\"send_email\",\"args\":{}}"));
        projector.ApplyResult(Result(
            "{\"tool_id\":\"two\",\"status\":\"error\",\"result\":\"smtp refused: 550 relay denied\\nat send()\"}"));

        // First line only — a traceback's remaining lines are one click away in the step's block.
        Assert.Equal("smtp refused: 550 relay denied", activity.LastErrorMessage);
        Assert.True(activity.ShowCollapsedError);

        activity.IsExpanded = true;

        Assert.False(activity.ShowCollapsedError);
    }

    /// <summary>
    /// A turn that ran exactly one tool drops the card wrapper and shows the step alone: no
    /// header to summarise a single row, no completion marker restating its outcome, and — since
    /// there is no disclosure control left — a timeline that cannot be folded shut.
    /// </summary>
    [Fact]
    public void SingleStep_DropsTheCardChrome_AndStaysVisible()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);

        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"read_file\",\"args\":{}}"));

        Assert.True(activity.IsSingleStep);
        Assert.False(activity.ShowCardChrome);
        // Collapsed on the headless pass, yet the one step still renders — otherwise the card
        // would be a permanently blank row with nothing to click.
        Assert.False(activity.IsExpanded);
        Assert.True(activity.IsTimelineVisible);
    }

    [Fact]
    public void CollapsedSingleStep_WithResolvedApproval_RemovesTimelineFromLayout()
    {
        var activity = new ToolActivityViewModel { IsExpanded = false };
        var projector = new ToolActivityProjector(activity);
        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"remote_bash\",\"args\":{}}"));
        var approval = new ChatMessage { EventKind = "approval", EventTitle = "Remote Bash" };
        activity.Approval = approval;
        approval.CompleteApproval(ApprovalAction.AllowOnce);

        Assert.True(activity.IsSingleStep);
        Assert.True(activity.ShowCardChrome);
        Assert.False(activity.IsExpanded);
        Assert.False(activity.IsTimelineVisible);
        Assert.True(activity.HasApproval);
    }

    /// <summary>A failed single step shows no collapsed error line: the step is already open and
    /// prints the same message in its own Error block.</summary>
    [Fact]
    public void SingleStep_ShowsNoCollapsedErrorLine()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"send_email\",\"args\":{}}"));
        projector.ApplyResult(Result("{\"tool_id\":\"one\",\"status\":\"error\",\"result\":\"smtp refused\"}"));

        Assert.Equal("smtp refused", activity.LastErrorMessage);
        Assert.False(activity.ShowCollapsedError);
    }

    /// <summary>The chrome comes back mid-turn when a second tool starts — which only works
    /// because the activity subscribes to its own Steps collection.</summary>
    [Fact]
    public void SecondStep_BringsTheChromeBack()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        var changed = new List<string>();
        activity.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"read_file\",\"args\":{}}"));
        Assert.False(activity.ShowCardChrome);

        projector.ApplyCall(Event("{\"tool_id\":\"two\",\"name\":\"send_email\",\"args\":{}}"));

        Assert.False(activity.IsSingleStep);
        Assert.True(activity.ShowCardChrome);
        // The view only re-reads it if the change was announced.
        Assert.Contains(nameof(ToolActivityViewModel.ShowCardChrome), changed);
    }

    /// <summary>An empty card keeps its chrome: it has nothing to show yet, and the header is the
    /// only thing saying so.</summary>
    [Fact]
    public void NoSteps_KeepsTheChrome()
    {
        var activity = new ToolActivityViewModel();

        Assert.False(activity.IsSingleStep);
        Assert.True(activity.ShowCardChrome);
    }

    [Fact]
    public void CollapsedErrorLine_IsAbsentWhenNothingFailed()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"read_file\",\"args\":{}}"));
        projector.ApplyResult(Result("{\"tool_id\":\"one\",\"status\":\"success\",\"result\":\"42 rows\"}"));

        Assert.Null(activity.LastErrorMessage);
        Assert.False(activity.ShowCollapsedError);
    }

    [Fact]
    public void AwaitingApproval_IsDrivenByTheLiveApproval_NotByStatus()
    {
        // A card with the parked status but NO live approval (the reload case) must not read as
        // waiting — otherwise history shows a stale "Waiting for approval".
        var reloaded = new ToolActivityViewModel { Status = ToolActivityStatus.WaitingForConfirmation };
        Assert.False(reloaded.IsAwaitingApproval);
        Assert.False(reloaded.HasApproval);

        // With a live, still-pending approval it reads as waiting…
        var live = new ToolActivityViewModel { Status = ToolActivityStatus.WaitingForConfirmation };
        var approval = new ChatMessage { EventKind = "approval", EventTitle = "Write" };
        live.Approval = approval;
        Assert.True(live.IsAwaitingApproval);

        // …and stops the moment the decision resolves, even though Status still says waiting (the
        // turn's Complete hasn't run yet). The activity must re-raise the derived flag.
        var raised = new System.Collections.Generic.List<string>();
        live.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        approval.CompleteApproval(ApprovalAction.AllowOnce);

        Assert.False(live.IsAwaitingApproval);
        Assert.Contains(nameof(ToolActivityViewModel.IsAwaitingApproval), raised);
    }

    [Fact]
    public void ReplacingApproval_UnsubscribesThePreviousApproval()
    {
        var activity = new ToolActivityViewModel { Status = ToolActivityStatus.WaitingForConfirmation };
        var previous = new ChatMessage { EventKind = "approval" };
        var current = new ChatMessage { EventKind = "approval" };
        activity.Approval = previous;
        activity.Approval = current;
        var raised = new System.Collections.Generic.List<string>();
        activity.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        previous.CompleteApproval(ApprovalAction.AllowOnce);
        Assert.Empty(raised);
        Assert.True(activity.IsAwaitingApproval);

        current.CompleteApproval(ApprovalAction.AllowOnce);
        Assert.Contains(nameof(ToolActivityViewModel.IsAwaitingApproval), raised);
        Assert.False(activity.IsAwaitingApproval);
    }

    private static ConnectOnion.Protocol.AgentStreamEvent Result(string json)
        => new("tool_result", "tool_result", null, json);

    [Fact]
    public void FailedStep_ShowsOneDetailBlockLabelledError()
    {
        // The projector writes the same text into Result and Error on failure, which is why the
        // view renders a single block rather than one card per field.
        var step = new ToolStepViewModel { Result = "connection refused", Error = "connection refused" };

        Assert.Equal("connection refused", step.DetailText);
        Assert.Equal("Error", step.DetailLabel);
        Assert.True(step.HasDetail);
    }

    [Fact]
    public void SucceededStep_ShowsTheResultLabelledResult()
    {
        var step = new ToolStepViewModel { Result = "42 rows" };

        Assert.Equal("42 rows", step.DetailText);
        Assert.Equal("Result", step.DetailLabel);
        Assert.True(step.HasDetail);
    }

    [Fact]
    public void StepWithNoOutput_RendersNoDetailBlock()
    {
        Assert.False(new ToolStepViewModel().HasDetail);
        Assert.False(new ToolStepViewModel { Result = "   " }.HasDetail);
    }

    private static ConnectOnion.Protocol.AgentStreamEvent Event(string json)
        => new("tool_call", "tool_call", null, json);

    [Fact]
    public void FailedRun_OnAPersistencePass_StaysCollapsed()
    {
        var activity = new ToolActivityViewModel();

        new ToolActivityProjector(activity).Complete(ToolActivityStatus.Failed, "Server closed connection");

        Assert.False(activity.IsExpanded);
    }

    [Fact]
    public void FailedStep_OnALiveView_OpensTheCard()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity, isLiveView: true);
        projector.ApplyCall(Event("{\"tool_id\":\"one\",\"name\":\"send_email\",\"args\":{}}"));
        projector.ApplyResult(new ConnectOnion.Protocol.AgentStreamEvent(
            "tool_result", "tool_result", null,
            "{\"tool_id\":\"one\",\"name\":\"send_email\",\"status\":\"error\",\"error\":\"smtp refused\"}"));

        projector.Complete();

        // Complete() rolls a single bad step up to PartialSuccess, not Failed — the expansion
        // rule has to look at the steps, or a failed tool would silently stay folded.
        Assert.True(activity.IsExpanded);
    }

    [Fact]
    public void FailedRun_StartsExpandedButStaysCollapsible()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity, isLiveView: true);

        // A dropped socket ("Server closed connection") completes the turn as Failed.
        projector.Complete(ToolActivityStatus.Failed, "Server closed connection");

        Assert.True(activity.IsExpanded);
        Assert.True(activity.IsTimelineVisible);

        activity.ToggleExpanded();

        Assert.False(activity.IsExpanded);
        Assert.False(activity.IsTimelineVisible);
        Assert.True(activity.IsVisible); // the failed bubble itself stays on screen
    }
}
