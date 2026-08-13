using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

/// <summary>
/// Where a pending decision ends up on screen.
///
/// <para>The decision controls sit at the bottom of the turn's one tool-activity card, below every
/// step it has already run. Expanding that card when an approval arrived — which is what it used to
/// do — meant the thing the agent was blocked on was pushed off the viewport by its own history,
/// and the card grew by hundreds of pixels at the same moment, shoving the transcript.</para>
/// </summary>
public class ApprovalCardHeightTests
{
    [Fact]
    public void ApprovalArriving_FoldsTheTimelineInsteadOfOpeningIt()
    {
        var (_, activity) = RunToolsThenRequestApproval(toolCalls: 8);

        Assert.True(activity.IsAwaitingApproval);
        Assert.False(activity.IsExpanded);
        Assert.False(activity.IsTimelineVisible);
    }

    /// <summary>Folding must not cost the card anything needed to answer: the header still names
    /// the tool and shows the badge, and the approval section is a sibling of the timeline, so it
    /// stays regardless.</summary>
    [Fact]
    public void FoldedApprovalCard_StillCarriesEverythingNeededToDecide()
    {
        var (approval, activity) = RunToolsThenRequestApproval(toolCalls: 8);

        // The header names the tool, which is how a folded card still says what is being approved.
        Assert.Equal(approval.EventTitle, activity.HeaderTitle);
        Assert.NotEmpty(activity.HeaderTitle);
        Assert.True(activity.IsAwaitingApproval);
        Assert.Same(approval, activity.Approval);
        Assert.True(activity.HasApproval);
        // ...and it says the turn did something first, which folding would otherwise hide.
        Assert.True(activity.ShowCollapsedStepCount);
        Assert.Equal("8 steps", activity.CollapsedStepCountHint);
    }

    /// <summary>The decision is the last row, whatever else the turn appended before it.
    ///
    /// <para>This is what promoting the approval to its own card bought. It used to be drawn by the
    /// tool-activity card, which is created at the turn's <i>first</i> tool call — so a turn that
    /// went on to append a plan review, a question and a couple of diffs left the live decision
    /// stranded above all of them, in the middle of the conversation with settled cards on both
    /// sides. Now it arrives where it happened, and because the host blocks until the user answers,
    /// that is the end of the transcript.</para></summary>
    [Fact]
    public void ApprovalIsTheLastVisibleRow_EvenAfterATurnFullOfOtherCards()
    {
        var target = new FakeTarget { IsLiveView = true };
        var projection = new ChatTurnProjection(target);

        projection.Apply(Event("tool_call",
            """{"tool_id":"t1","name":"remote_bash","args":{"command":"ls"}}"""));
        projection.Apply(Event("tool_result",
            """{"tool_id":"t1","status":"success","result":"ok"}"""));
        projection.Apply(Event("plan_review", """{"plan_content":"1. Inspect"}"""));
        projection.Apply(Event("ask_user",
            """{"id":"ask-1","question":"Which environment?","options":["staging","prod"]}"""));
        projection.Apply(Event("diff_preview",
            """{"type":"diff_preview","path":"a.txt","preview":"+new","file_exists":true}"""));
        projection.Apply(Event("diff_preview",
            """{"type":"diff_preview","path":"b.txt","preview":"+new","file_exists":true}"""));

        projection.Apply(Event("approval_needed",
            """{"tool":"remote_bash","arguments":{"command":"rm -rf build"}}"""));

        var approval = target.Messages.Single(message => message.IsApprovalEvent);
        var visible = target.Messages.Where(message => message.IsTranscriptRowVisible).ToList();

        Assert.True(approval.IsTranscriptRowVisible);
        Assert.Same(approval, visible[^1]);
        // The tool card is well above it, and still says what it is waiting on.
        var toolCard = target.Messages.Single(message => message.IsToolActivityEvent);
        Assert.True(target.Messages.IndexOf(toolCard) < target.Messages.IndexOf(approval));
        Assert.True(toolCard.ToolActivity!.IsAwaitingApproval);
    }

    [Fact]
    public void SingleStepApproval_SaysOneStep()
    {
        var (_, activity) = RunToolsThenRequestApproval(toolCalls: 1);
        Assert.Equal("1 step", activity.CollapsedStepCountHint);
    }

    /// <summary>The step count is a folded-card affordance only — opening the timeline shows the
    /// steps themselves, so restating the count would be noise.</summary>
    [Fact]
    public void ExpandingTheTimeline_DropsTheStepCountHint()
    {
        var (_, activity) = RunToolsThenRequestApproval(toolCalls: 3);

        activity.ToggleExpanded();

        Assert.True(activity.IsExpanded);
        Assert.False(activity.ShowCollapsedStepCount);
    }

    /// <summary>A user who opened the timeline keeps it open — auto-expansion stops steering the
    /// card once they have said what they want, which is why folding on approval cannot fight
    /// them.</summary>
    [Fact]
    public void UserExpansion_SurvivesTheNextApproval()
    {
        var (_, activity) = RunToolsThenRequestApproval(toolCalls: 2);
        activity.ToggleExpanded();
        Assert.True(activity.IsExpanded);

        // A second approval in the same turn re-runs auto-expansion.
        activity.Approval!.CompleteApproval(ApprovalAction.AllowOnce);
        Assert.True(activity.IsExpanded);
    }

    /// <summary>Once the decision is answered the card goes back to the ordinary running rule, so
    /// the timeline reopens and the turn stays observable.</summary>
    [Fact]
    public void AnsweringTheApproval_ReopensTheTimeline()
    {
        var target = new FakeTarget { IsLiveView = true };
        var projection = new ChatTurnProjection(target);
        projection.Apply(Event("tool_call", """{"tool_id":"t1","name":"remote_bash","args":{"command":"ls"}}"""));
        projection.Apply(Event("approval_needed", """{"tool":"remote_bash","arguments":{"command":"ls"}}"""));

        var activity = target.Messages.Single(m => m.IsToolActivityEvent).ToolActivity!;
        Assert.False(activity.IsExpanded);

        // A result arriving is what re-runs auto-expansion after the decision.
        activity.Approval!.CompleteApproval(ApprovalAction.AllowOnce);
        projection.Apply(Event("tool_result", """{"tool_id":"t1","status":"success","result":"ok"}"""));
        projection.Apply(Event("tool_call", """{"tool_id":"t2","name":"remote_bash","args":{"command":"pwd"}}"""));

        Assert.False(activity.IsAwaitingApproval);
        Assert.True(activity.IsExpanded);
    }

    private static (ChatMessage Approval, ToolActivityViewModel Activity) RunToolsThenRequestApproval(int toolCalls)
    {
        var target = new FakeTarget { IsLiveView = true };
        var projection = new ChatTurnProjection(target);
        for (var i = 0; i < toolCalls; i++)
        {
            projection.Apply(Event("tool_call",
                $$$"""{"tool_id":"t{{{i}}}","name":"remote_bash","args":{"command":"echo {{{i}}}"}}"""));
        }

        projection.Apply(Event("approval_needed",
            """{"tool":"remote_bash","arguments":{"command":"git clean -d -f"},"reason":"destructive"}"""));

        var activity = target.Messages.Single(m => m.IsToolActivityEvent).ToolActivity!;
        return (activity.Approval!, activity);
    }

    private static AgentStreamEvent Event(string type, string json) => new(type, type, null, json);

    private sealed class FakeTarget : IChatProjectionTarget
    {
        private long _nextId = 1;
        public IList<ChatMessage> Messages { get; } = new List<ChatMessage>();
        public string? AgentName => "Test Agent";
        public bool IsLiveView { get; set; }
        public long NextId() => _nextId++;
        public void Add(ChatMessage message) => Messages.Add(message);
        public void ResolveAgentImage(string dataUrl, ChatAttachment attachment)
            => attachment.Status = AttachmentStatus.Sent;
        public void ReportUsage(TurnUsage usage) { }
    }
}
