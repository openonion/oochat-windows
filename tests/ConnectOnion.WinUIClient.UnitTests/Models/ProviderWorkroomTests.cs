using System.Text.Json;
using System.Text.Json.Nodes;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class ProviderWorkRoomTests
{
    private const string RoomId = "wr-1";

    private const string Invocation = """{"workroomId":"wr-1","provider":"claude","invocationId":"inv-1","parentToolCallId":"call-1","title":"Fix the login bug","status":"running","stateRevision":1,"capabilities":["provider_continuation","provider_interrupt","provider_permission_change"]}""";

    private const string PermissionOptionsJson = """[{"id":"read","label":"Read only","risk":"low","selectable":true},{"id":"write","label":"Workspace write","risk":"medium","selectable":true},{"id":"admin","disabled":true,"disabledSummary":"Blocked by policy"},{"label":"missing id"},"not-an-object"]""";

    private static bool Apply(ProviderWorkRoom room, string type, string json, string? correlatedId = null)
    {
        using var document = JsonDocument.Parse(json);
        return room.Apply(type, document.RootElement, correlatedId);
    }

    private static ProviderWorkRoom InvokedRoom()
    {
        var room = new ProviderWorkRoom();
        Assert.True(Apply(room, "provider_invocation", Invocation));
        return room;
    }

    private static string Message(string id, string role, string text, long sequence = 1, long revision = 0)
        => $$"""{"workroomId":"wr-1","messageId":"{{id}}","role":"{{role}}","text":"{{text}}","sequence":{{sequence}},"revision":{{revision}}}""";

    private static string Activity(IEnumerable<string> paths, string? status = null)
    {
        var activity = new JsonObject { ["files"] = new JsonArray(paths.Select(path => (JsonNode?)path).ToArray()) };
        if (status is not null) activity["status"] = status;
        return new JsonObject { ["workroomId"] = RoomId, ["activity"] = activity }.ToJsonString();
    }

    private static string InvocationWithPermission(long stateRevision, long effectiveRevision, string activeOptionId)
        => new JsonObject
        {
            ["workroomId"] = RoomId,
            ["invocationId"] = "inv-1",
            ["stateRevision"] = stateRevision,
            ["capabilities"] = new JsonArray("provider_permission_change"),
            ["providerPermission"] = new JsonObject
            {
                ["activeOptionId"] = activeOptionId,
                ["effectiveRevision"] = effectiveRevision,
                ["options"] = JsonNode.Parse(PermissionOptionsJson),
            },
        }.ToJsonString();

    private static string PermissionAck(string requestId, bool? accepted, string optionId,
        long? effectiveRevision = null, string? activeOptionId = null, string? status = null)
    {
        var ack = new JsonObject { ["workroomId"] = RoomId, ["requestId"] = requestId, ["optionId"] = optionId };
        if (accepted is { } value) ack["accepted"] = value;
        if (status is not null) ack["status"] = status;
        if (effectiveRevision is { } revision)
            ack["providerPermission"] = new JsonObject
            {
                ["activeOptionId"] = activeOptionId,
                ["effectiveRevision"] = revision,
                ["options"] = JsonNode.Parse(PermissionOptionsJson),
            };
        return ack.ToJsonString();
    }

    private static ProviderWorkRoom RoomWithPermission()
    {
        var room = new ProviderWorkRoom();
        Assert.True(Apply(room, "provider_invocation", InvocationWithPermission(2, 2, "read")));
        return room;
    }

    // Tests for events, creation, and labels

    [Theory]
    [InlineData("provider_session", true)]
    [InlineData("provider_invocation", true)]
    [InlineData("provider_activity", true)]
    [InlineData("provider_approval", true)]
    [InlineData("provider_artifact", true)]
    [InlineData("provider_continuation", true)]
    [InlineData("provider_input_ack", true)]
    [InlineData("provider_interrupt", true)]
    [InlineData("provider_error", true)]
    [InlineData("provider_message", true)]
    [InlineData("provider_phase", true)]
    [InlineData("provider_permission_ack", true)]
    [InlineData("tool_call", false)]
    [InlineData("Provider_Message", false)]
    public void IsWorkRoomEvent_MatchesDocumentedEventsOnly(string type, bool expected)
        => Assert.Equal(expected, ProviderWorkRoom.IsWorkRoomEvent(type));

    [Fact]
    public void Apply_ProviderInvocation_CreatesRoomState()
    {
        var room = InvokedRoom();

        Assert.Equal(RoomId, room.WorkRoomId);
        Assert.Equal("Claude Code", room.ProviderDisplayName);
        Assert.Equal("Fix the login bug", room.Title);
        Assert.Equal("inv-1", room.CurrentInvocationId);
        Assert.Equal("call-1", room.ParentToolCallId);
        Assert.Equal("running", room.Status);
        Assert.Equal(1L, room.StateRevision);
        Assert.True(room.HasAuthoritativeState);
        Assert.Equal(3, room.Capabilities.Count);
    }

    [Fact]
    public void Apply_ProviderSession_SetsParentSessionAndDisplayName()
    {
        var room = new ProviderWorkRoom();

        Apply(room, "provider_session", """{"workroomId":"wr-1","displayName":"Team Codex","parentSessionId":"conv-9","providerSessionRef":"sess-1"}""");

        Assert.Equal("Team Codex", room.ProviderDisplayName);
        Assert.Equal("conv-9", room.ParentConversationId);
        Assert.Equal("sess-1", room.ProviderSessionRef);
    }

    [Fact]
    public void Apply_UsesCorrelatedId_WhenEventHasNoExplicitId()
    {
        var room = new ProviderWorkRoom();

        Assert.True(Apply(room, "provider_message", """{"messageId":"m1","role":"assistant","text":"hi"}""", correlatedId: RoomId));
        Assert.Equal(RoomId, room.WorkRoomId);
    }

    [Theory]
    [InlineData("codex", "Codex")]
    [InlineData("claude_code", "Claude Code")]
    [InlineData("gemini", "gemini")]
    [InlineData("   ", "Coding agent")]
    public void ProviderLabel_MapsKnownProvidersAndFallsBack(string provider, string expected)
        => Assert.Equal(expected, ProviderWorkRoom.ProviderLabel(provider));

    [Theory]
    [InlineData("running", "running")]
    [InlineData("exploding", "unavailable")]
    [InlineData(null, "unavailable")]
    public void NormalizeStatus_KeepsSupportedStatusesOnly(string? status, string expected)
        => Assert.Equal(expected, ProviderWorkRoom.NormalizeStatus(status));

    [Theory]
    [InlineData("starting", "Starting")]
    [InlineData("running", "Working")]
    [InlineData("thinking", "Thinking")]
    [InlineData("awaiting_approval", "Approval needed")]
    [InlineData("awaiting_input", "Waiting for input")]
    [InlineData("stopping", "Stopping")]
    [InlineData("completed", "Completed")]
    [InlineData("failed", "Failed")]
    [InlineData("cancelled", "Stopped")]
    public void StatusLabel_RoundTripsThroughStatusFromLabel(string status, string label)
    {
        Assert.Equal(label, ProviderWorkRoom.StatusLabel(status));
        Assert.Equal(status, ProviderWorkRoom.StatusFromLabel(label));
    }

    [Fact]
    public void StatusLabels_UnknownValues_FallBack()
    {
        Assert.Equal("Unavailable", ProviderWorkRoom.StatusLabel("nonsense"));
        Assert.Null(ProviderWorkRoom.StatusFromLabel("Nonsense"));
    }

    // Tests for ids and revisions

    [Theory]
    [InlineData("""{"provider":"codex"}""")]
    [InlineData("""{"workroomId":"   "}""")]
    [InlineData("""{"workroomId":"wr\u0001bad"}""")]
    [InlineData("""["wr-1"]""")]
    public void Apply_InvalidIdOrPayload_IsRejected(string json)
    {
        var room = new ProviderWorkRoom();

        Assert.False(Apply(room, "provider_session", json));
        Assert.Equal("", room.WorkRoomId);
    }

    [Fact]
    public void Apply_OverlongId_IsTruncatedTo128Characters()
    {
        var room = new ProviderWorkRoom();

        Assert.True(Apply(room, "provider_session", $$"""{"workroomId":"{{new string('a', 200)}}"}"""));
        Assert.Equal(128, room.WorkRoomId.Length);
    }

    [Fact]
    public void Apply_MismatchedId_CannotUpdateExistingRoom()
    {
        var room = InvokedRoom();

        Assert.False(Apply(room, "provider_phase", """{"workroomId":"wr-2","phase":"completed","stateRevision":9}"""));
        Assert.Equal("running", room.Status);
    }

    [Fact]
    public void Apply_NewerRevisionUpdates_StaleRevisionIsIgnored()
    {
        var room = InvokedRoom();

        Assert.True(Apply(room, "provider_phase", """{"workroomId":"wr-1","phase":"thinking","stateRevision":5}"""));
        Assert.False(Apply(room, "provider_phase", """{"workroomId":"wr-1","phase":"completed","stateRevision":3}"""));

        Assert.Equal("thinking", room.Status);
        Assert.Equal(5L, room.StateRevision);
    }

    // Tests capability rules

    [Theory]
    [InlineData("ok", true)]
    [InlineData("provider-input", true)]
    [InlineData("session-ref-only", true)]
    [InlineData("completed", true)] // only failed, cancelled, and unavailable block follow-ups
    [InlineData("not-authoritative", false)]
    [InlineData("zero-revision", false)]
    [InlineData("no-capability", false)]
    [InlineData("no-target", false)]
    [InlineData("failed", false)]
    [InlineData("cancelled", false)]
    public void CanContinue_RequiresAuthorityRevisionCapabilityTargetAndLiveStatus(string scenario, bool expected)
    {
        var room = new ProviderWorkRoom
        {
            HasAuthoritativeState = true,
            StateRevision = 1,
            Capabilities = ["provider_continuation"],
            CurrentInvocationId = "inv-1",
            Status = "running",
        };
        switch (scenario)
        {
            case "provider-input": room.Capabilities = ["provider_input"]; break;
            case "session-ref-only": room.CurrentInvocationId = null; room.ProviderSessionRef = "sess-1"; break;
            case "not-authoritative": room.HasAuthoritativeState = false; break;
            case "zero-revision": room.StateRevision = 0; break;
            case "no-capability": room.Capabilities = []; break;
            case "no-target": room.CurrentInvocationId = null; break;
            case "completed" or "failed" or "cancelled": room.Status = scenario; break;
        }

        Assert.Equal(expected, room.CanContinue);
    }

    [Theory]
    [InlineData("running", true, true, true)]
    [InlineData("awaiting_approval", true, true, true)]
    [InlineData("completed", true, true, false)]
    [InlineData("running", false, true, false)]
    [InlineData("running", true, false, false)]
    public void CanStop_RequiresInterruptCapabilityAuthorityAndRunningStatus(string status, bool capability, bool authoritative, bool expected)
    {
        var room = new ProviderWorkRoom
        {
            Status = status,
            HasAuthoritativeState = authoritative,
            Capabilities = capability ? ["provider_interrupt"] : [],
        };

        Assert.Equal(expected, room.CanStop);
    }

    [Theory]
    [InlineData("ok", true)]
    [InlineData("no-permission", false)]
    [InlineData("zero-revision", false)]
    [InlineData("unavailable-summary", false)]
    [InlineData("nothing-selectable", false)]
    [InlineData("no-capability", false)]
    [InlineData("change-unavailable", false)]
    [InlineData("ack-rejected", false)]
    [InlineData("no-invocation", false)]
    public void CanChangePermission_RequiresEveryCondition(string scenario, bool expected)
    {
        static ProviderWorkRoomPermissionOption Option(string id, bool selectable) => new(id, null, id, null, null, "low", selectable, null);
        var permission = new ProviderWorkRoomPermission("read", 2, null, null, [], [Option("read", true), Option("write", true)]);
        var room = new ProviderWorkRoom
        {
            HasAuthoritativeState = true,
            CurrentInvocationId = "inv-1",
            Capabilities = ["provider_permission_change"],
            Permission = permission,
        };
        switch (scenario)
        {
            case "no-permission": room.Permission = null; break;
            case "zero-revision": room.Permission = permission with { EffectiveRevision = 0 }; break;
            case "unavailable-summary": room.Permission = permission with { UnavailableSummary = "Locked" }; break;
            case "nothing-selectable": room.Permission = permission with { Options = [Option("read", false)] }; break;
            case "no-capability": room.Capabilities = []; break;
            case "change-unavailable": room.PermissionChangeUnavailable = true; break;
            case "ack-rejected": room.PermissionAckAccepted = false; break;
            case "no-invocation": room.CurrentInvocationId = null; break;
        }

        Assert.Equal(expected, room.CanChangePermission);
    }

    // Tests messages

    [Fact]
    public void ProviderMessage_ValidMessage_IsAdded()
    {
        var room = InvokedRoom();

        Apply(room, "provider_message", Message("m1", "assistant", "Done."));

        var message = Assert.Single(room.Messages);
        Assert.True(message.IsAssistant);
        Assert.Equal("Done.", message.Text);
        Assert.Equal("delivered", message.DeliveryStatus);
    }

    [Theory]
    [InlineData("""{"workroomId":"wr-1","messageId":"m1","role":"system","text":"x"}""")]
    [InlineData("""{"workroomId":"wr-1","role":"assistant","text":"x"}""")]
    [InlineData("""{"workroomId":"wr-1","messageId":"m1","role":"assistant","text":"   "}""")]
    public void ProviderMessage_InvalidRoleOrMissingFields_IsIgnored(string json)
    {
        var room = InvokedRoom();

        Apply(room, "provider_message", json);

        Assert.Empty(room.Messages);
    }

    [Fact]
    public void ProviderMessage_HigherRevisionReplaces_LowerRevisionDoesNot()
    {
        var room = InvokedRoom();

        Apply(room, "provider_message", Message("m1", "assistant", "draft", revision: 1));
        Apply(room, "provider_message", Message("m1", "assistant", "final", revision: 2));
        Apply(room, "provider_message", Message("m1", "assistant", "stale", revision: 1));

        Assert.Equal("final", Assert.Single(room.Messages).Text);
    }

    [Fact]
    public void ProviderMessages_AreOrderedBySequence_UserFirstOnTies()
    {
        var room = InvokedRoom();

        Apply(room, "provider_message", Message("a2", "assistant", "x", sequence: 2));
        Apply(room, "provider_message", Message("a1", "assistant", "x", sequence: 1));
        Apply(room, "provider_message", Message("u1", "user", "x", sequence: 1));

        Assert.Equal(new[] { "u1", "a1", "a2" }, room.Messages.Select(message => message.Id));
    }

    [Fact]
    public void ProviderMessages_AreCappedAtMaxMessages_KeepingNewest()
    {
        var room = InvokedRoom();

        for (var i = 0; i < ProviderWorkRoom.MaxMessages + 5; i++)
            Apply(room, "provider_message", Message($"m{i}", "assistant", "text", sequence: i));

        Assert.Equal(ProviderWorkRoom.MaxMessages, room.Messages.Count);
        Assert.Equal(5L, room.Messages[0].Sequence);
    }

    [Fact]
    public void Acknowledgements_UpdateOnlyTheMatchingClientMessage()
    {
        var room = InvokedRoom();
        room.Messages.Add(new ProviderWorkRoomMessage("local-1", "user", "Also add tests", 10, "final", "client-1", "queued"));

        Apply(room, "provider_input_ack", """{"workroomId":"wr-1","clientMessageId":"client-unknown"}""");
        Assert.Equal("queued", room.Messages[0].DeliveryStatus);

        Apply(room, "provider_input_ack", """{"workroomId":"wr-1","clientMessageId":"client-1"}""");
        Assert.Equal("Sent to host", room.Messages[0].DeliveryLabel);

        Apply(room, "provider_continuation", """{"workroomId":"wr-1","clientMessageId":"client-1","lifecycle":"accepted"}""");
        Assert.Equal("accepted", room.Messages[0].DeliveryStatus);
    }

    // Tests files

    [Fact]
    public void Activity_CollectsNormalizesAndDeduplicatesPaths()
    {
        var room = InvokedRoom();

        Apply(room, "provider_activity",
            """{"workroomId":"wr-1","activity":{"status":"modified","files":["src/a.cs","src\\b.cs",{"path":"src/c.cs"},"SRC/A.CS"]}}""");
        Apply(room, "provider_activity", Activity(["src/a.cs"], "deleted"));

        Assert.Equal(new[] { "src/a.cs", "src/b.cs", "src/c.cs" }, room.Files.Select(file => file.Path));
        Assert.Equal("deleted", room.Files[0].Status);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("../secret.txt")]
    [InlineData("src/../../secret.txt")]
    [InlineData("src//double-slash.txt")]
    public void Activity_RejectsUnsafePaths(string path)
    {
        var room = InvokedRoom();

        Apply(room, "provider_activity", Activity([path]));

        Assert.Empty(room.Files);
    }

    [Fact]
    public void Activity_CapsFilesAt100()
    {
        var room = InvokedRoom();

        Apply(room, "provider_activity", Activity(Enumerable.Range(0, 150).Select(i => $"f{i}.txt")));

        Assert.Equal(100, room.Files.Count);
    }

    [Fact]
    public void Artifact_AddsFiles()
    {
        var room = InvokedRoom();

        Apply(room, "provider_artifact", """{"workroomId":"wr-1","artifact":{"files":["out/report.md"]}}""");

        Assert.Equal("out/report.md", Assert.Single(room.Files).Path);
    }

    // Tests for approvals

    private const string PendingApproval = """{"workroomId":"wr-1","requestId":"ap-1","summary":"Run npm install","allowedOptions":["approve","reject"],"stateRevision":2}""";

    [Fact]
    public void Approval_PendingThenResolved_UpdatesApprovalAndStatus()
    {
        var room = InvokedRoom();

        Apply(room, "provider_approval", PendingApproval);
        var pending = Assert.Single(room.Approvals);
        Assert.True(pending.IsPending);
        Assert.True(pending.AllowsApprove);
        Assert.True(pending.AllowsReject);
        Assert.Equal("awaiting_approval", room.Status);

        Apply(room, "provider_approval", """{"workroomId":"wr-1","requestId":"ap-1","resolution":"approved","stateRevision":3}""");
        Assert.False(Assert.Single(room.Approvals).IsPending);
        Assert.Equal("running", room.Status);

        Assert.False(Apply(room, "provider_approval", PendingApproval));
        Assert.False(Assert.Single(room.Approvals).IsPending);
    }

    [Fact]
    public void Approval_WithoutRequestId_IsIgnored()
    {
        var room = InvokedRoom();

        Apply(room, "provider_approval", """{"workroomId":"wr-1","summary":"Run rm -rf","stateRevision":2}""");

        Assert.Empty(room.Approvals);
    }

    // Tests for permissions

    [Fact]
    public void PermissionSnapshot_ParsesValidOptionsAndSkipsInvalidOnes()
    {
        var room = RoomWithPermission();

        var permission = room.Permission!;
        Assert.Equal("read", permission.ActiveOptionId);
        Assert.Equal(new[] { "read", "write", "admin" }, permission.Options.Select(option => option.Id));
        Assert.Equal("Read only", permission.Options[0].Label);
        Assert.False(permission.Options[2].Selectable);
        Assert.True(room.CanChangePermission);
    }

    [Fact]
    public void PermissionSnapshot_StaleIsIgnored_NewerReplaces()
    {
        var room = RoomWithPermission();

        Apply(room, "provider_invocation", InvocationWithPermission(3, 1, "write"));
        Assert.Equal("read", room.Permission!.ActiveOptionId);

        Apply(room, "provider_invocation", InvocationWithPermission(4, 3, "write"));
        Assert.Equal("write", room.Permission!.ActiveOptionId);
    }

    [Fact]
    public void PermissionSnapshot_WithoutStateRevision_SuppliesRoomRevision()
    {
        var room = new ProviderWorkRoom();

        Apply(room, "provider_invocation",
            """{"workroomId":"wr-1","providerPermission":{"activeOptionId":"read","effectiveRevision":4,"options":[{"id":"read","selectable":true}]}}""");

        Assert.Equal(4L, room.StateRevision);
    }

    [Fact]
    public void PermissionAck_SuccessWithNewerMatchingSnapshot_UpdatesProfile()
    {
        var room = RoomWithPermission();

        Apply(room, "provider_permission_ack", PermissionAck("ack-1", true, "write", 3, "write"));

        Assert.Equal("write", room.Permission!.ActiveOptionId);
        Assert.True(room.PermissionAckAccepted);
        Assert.False(room.PermissionChangeUnavailable);
        Assert.Equal(3L, room.StateRevision);
    }

    [Theory]
    [InlineData("applied", true)]
    [InlineData("denied", false)]
    public void PermissionAck_ReadsResultFromStatusWord(string status, bool expected)
    {
        var room = RoomWithPermission();

        Apply(room, "provider_permission_ack", PermissionAck("ack-1", null, "write", 3, "write", status));

        Assert.Equal(expected, room.PermissionAckAccepted);
    }

    [Fact]
    public void PermissionAck_Rejected_KeepsProfileAndDisablesChanges()
    {
        var room = RoomWithPermission();

        Apply(room, "provider_permission_ack", PermissionAck("ack-1", false, "write"));

        Assert.Equal("The host rejected the permission change.", room.PermissionAckError);
        Assert.Equal("read", room.Permission!.ActiveOptionId);
        Assert.False(room.CanChangePermission);
    }

    [Theory]
    [InlineData(null, null)]      // no snapshot
    [InlineData(2L, "write")]     // not newer
    [InlineData(3L, "full")]      // active option doesn't match
    public void PermissionAck_WithoutConfirmedNewerState_DoesNotReportSuccess(long? revision, string? activeOptionId)
    {
        var room = RoomWithPermission();

        Apply(room, "provider_permission_ack", PermissionAck("ack-1", true, "write", revision, activeOptionId));

        Assert.False(room.PermissionAckAccepted);
        Assert.Equal("The host did not return a newer permission state.", room.PermissionAckError);
        Assert.Equal("read", room.Permission!.ActiveOptionId);
    }

    [Fact]
    public void PermissionAck_Duplicate_IsIgnored()
    {
        var room = RoomWithPermission();

        Apply(room, "provider_permission_ack", PermissionAck("ack-1", false, "write"));
        Apply(room, "provider_permission_ack", PermissionAck("ack-1", true, "write", 3, "write"));

        Assert.Equal(1L, room.PermissionAckCounter);
        Assert.False(room.PermissionAckAccepted);
    }

    // Tests for cloning

    [Fact]
    public void Clone_MutableListsAreIndependentOfOriginal()
    {
        var room = RoomWithPermission();
        Apply(room, "provider_message", Message("m1", "assistant", "hi"));
        Apply(room, "provider_approval", """{"workroomId":"wr-1","requestId":"ap-1","allowedOptions":["approve"],"stateRevision":3}""");

        var clone = room.Clone();
        clone.Messages.Clear();
        clone.Capabilities.Clear();
        clone.Approvals[0].AllowedOptions.Add("reject");
        clone.Permission!.Options.Clear();

        Assert.Single(room.Messages);
        Assert.Single(room.Capabilities);
        Assert.Equal(new[] { "approve" }, room.Approvals[0].AllowedOptions);
        Assert.Equal(3, room.Permission!.Options.Count);
    }
}

