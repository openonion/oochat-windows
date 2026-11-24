using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

public sealed class WorkRoomCardProjectionTests
{
    private readonly FakeTarget _target = new();
    private readonly ChatTurnProjection _projection;

    public WorkRoomCardProjectionTests() => _projection = new ChatTurnProjection(_target);

    private void Apply(string type, string json, bool replay = false)
        => _projection.Apply(new AgentStreamEvent(type, type, null, json) { IsSessionSyncReplay = replay });

    private ChatMessage Card => Assert.Single(_target.Messages, message => message.IsWorkRoomEvent);

    private ChatMessage Activity => Assert.Single(_target.Messages, message => message.EventKind == "activity");

    // Tests workroom cards

    [Fact]
    public void ProviderSession_CreatesWorkRoomCard()
    {
        Apply("provider_session", """{"workroomId":"wr-1","provider":"codex","status":"starting"}""");

        var card = Card;
        Assert.Equal("wr-1", card.EventKey);
        Assert.Equal("Codex", card.EventEyebrow);
        Assert.Equal("Coding Workroom", card.EventTitle);
        Assert.Equal("Codex · Starting", card.EventMeta);
        Assert.Equal(EventStatus.Running, card.Status);
    }

    [Fact]
    public void LaterEvents_UpdateTheSameCard()
    {
        Apply("provider_session", """{"workroomId":"wr-1","provider":"claude"}""");
        Apply("provider_invocation", """{"workroomId":"wr-1","provider":"claude","task_title":"Fix the login bug","status":"running","currentSummary":"Reading files"}""");

        var card = Card;
        Assert.Equal("Fix the login bug", card.EventTitle);
        Assert.Equal("Reading files", card.EventDetail);
        Assert.Equal("Claude Code · Working", card.EventMeta);
    }

    [Fact]
    public void Phase_Completed_MarksCardDone()
    {
        Apply("provider_invocation", """{"workroomId":"wr-1","provider":"codex","status":"running"}""");

        Apply("provider_phase", """{"workroomId":"wr-1","provider":"codex","phase":"completed","resultSummary":"All tests pass"}""");

        Assert.Equal(EventStatus.Done, Card.Status);
        Assert.Equal("Codex · Completed", Card.EventMeta);
        Assert.Equal("All tests pass", Card.EventDetail);
    }

    [Fact]
    public void Approval_PendingThenResolved_ReturnsToWorking()
    {
        Apply("provider_invocation", """{"workroomId":"wr-1","provider":"codex","status":"running"}""");

        Apply("provider_approval", """{"workroomId":"wr-1","provider":"codex","summary":"Run npm install"}""");
        Assert.Equal("Codex · Approval needed", Card.EventMeta);
        Assert.Equal("Run npm install", Card.EventDetail);

        Apply("provider_approval", """{"workroomId":"wr-1","provider":"codex","resolution":"approved"}""");
        Assert.Equal("Codex · Working", Card.EventMeta);
    }

    [Theory]
    [InlineData("""{"workroomId":"wr-1","safe_summary":"The provider crashed"}""", "The provider crashed", "Failed")]
    [InlineData("""{"workroomId":"wr-1","status":"cancelled"}""", "Workroom failed.", "Stopped")]
    public void ProviderError_MarksCardAsError(string json, string detail, string label)
    {
        Apply("provider_invocation", """{"workroomId":"wr-1","status":"running"}""");

        Apply("provider_error", json);

        Assert.Equal(EventStatus.Error, Card.Status);
        Assert.Equal(detail, Card.EventDetail);
        Assert.Equal($"Coding agent · {label}", Card.EventMeta);
    }

    [Fact]
    public void Interrupt_Cancelled_MarksCardAsError()
    {
        Apply("provider_invocation", """{"workroomId":"wr-1","provider":"gemini","status":"running"}""");

        Apply("provider_interrupt", """{"workroomId":"wr-1","provider":"gemini","status":"cancelled"}""");

        Assert.Equal(EventStatus.Error, Card.Status);
        Assert.Equal("gemini · Stopped", Card.EventMeta);
    }

    [Fact]
    public void LongTitle_IsCappedAt200Characters()
    {
        Apply("provider_invocation", $$"""{"workroomId":"wr-1","title":"{{new string('x', 300)}}"}""");

        Assert.Equal(200, Card.EventTitle!.Length);
    }

    [Theory]
    [InlineData("""{"provider":"codex"}""", false)]
    [InlineData("""{"workroomId":"wr-1","provider":"codex"}""", true)]
    public void MissingIdOrReplayOfUnknownRoom_AddsNoCard(string json, bool replay)
    {
        Apply("provider_session", json, replay);

        Assert.Empty(_target.Messages);
    }

    // Tests for intent, eval, compact

    [Fact]
    public void Intent_StartThenComplete_UpdatesOneRow()
    {
        Apply("intent", """{"id":"i1","status":"analyzing"}""");
        Assert.Equal("Understanding request...", Activity.EventTitle);

        Apply("intent", """{"id":"i1","status":"done","ack":"Refactor the parser"}""");

        Assert.Equal("Request understood", Activity.EventTitle);
        Assert.Equal("Refactor the parser", Activity.EventDetail);
        Assert.Equal(EventStatus.Done, Activity.Status);
    }

    [Theory]
    [InlineData("""{"id":"e1","status":"done","passed":false,"summary":"Missed a case"}""", "Missed a case", EventStatus.Error)]
    [InlineData("""{"id":"e1","status":"done","passed":true,"expected":"All tests pass"}""", "All tests pass", EventStatus.Done)]
    public void Eval_StartThenComplete_UpdatesOneRow(string completion, string detail, EventStatus status)
    {
        Apply("eval", """{"id":"e1","status":"evaluating","expected":"All tests pass"}""");

        Apply("eval", completion);

        Assert.Equal("Evaluation complete", Activity.EventTitle);
        Assert.Equal(detail, Activity.EventDetail);
        Assert.Equal(status, Activity.Status);
    }

    [Theory]
    [InlineData("""{"id":"c1","status":"done","message":"Trimmed history","context_before":1000,"context_after":400}""", EventStatus.Done)]
    [InlineData("""{"id":"c1","status":"error","error":"Out of memory"}""", EventStatus.Error)]
    public void Compact_StartThenComplete_UpdatesOneRow(string completion, EventStatus status)
    {
        Apply("compact", """{"id":"c1","status":"compacting"}""");

        Apply("compact", completion);

        Assert.Equal("Context compacted", Activity.EventTitle);
        Assert.Equal(status, Activity.Status);
        if (status == EventStatus.Done)
        {
            Assert.Contains("1000", Activity.EventMeta);
            Assert.Contains("400", Activity.EventMeta);
        }
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("eval")]
    [InlineData("compact")]
    public void Completion_WithoutAStartedRow_IsDropped(string type)
    {
        Apply(type, """{"id":"never-started","status":"done"}""");

        Assert.Empty(_target.Messages);
    }

    [Fact]
    public void FilesReceived_ListsTheFileNames()
    {
        Apply("files_received", """{"type":"files_received","files":[{"name":"a.txt","path":"/tmp/a.txt"},{"name":"b.csv"},{"path":"no-name"}]}""");

        Assert.Equal("Files received - 2 file(s)", Activity.EventTitle);
        Assert.Equal("a.txt, b.csv", Activity.EventDetail);
    }

    private sealed class FakeTarget : IChatProjectionTarget
    {
        private long _nextId = 1;
        public IList<ChatMessage> Messages { get; } = new List<ChatMessage>();
        public string? AgentName => "Test Agent";
        public bool IsLiveView { get; set; }
        public long NextId() => _nextId++;
        public void Add(ChatMessage message) => Messages.Add(message);
        public void ResolveAgentImage(string dataUrl, ChatAttachment attachment) => attachment.Status = AttachmentStatus.Sent;
        public void ReportUsage(TurnUsage usage) { }
    }
}

