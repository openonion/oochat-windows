using System.Net.Sockets;
using System.Net.WebSockets;
using ConnectOnion.Protocol.Tests.Fakes;

namespace ConnectOnion.Protocol.Tests;

public sealed class AgentConnectionServiceTests
{
    [Fact]
    public async Task SendInputAsync_NormalHandshake_ReturnsOutputAndExpectedWireFrames()
    {
        string? connectJson = null;
        string? inputJson = null;
        var releaseServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            connectJson = await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\",\"session_id\":\"session-1\"}", ct);
            inputJson = await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"done\",\"duration_ms\":42}", ct);
            await releaseServer.Task.WaitAsync(ct);
        });
        await using var service = Service(server);

        var result = await service.SendInputAsync("hello", null);

        Assert.Equal("done", result);
        Assert.True(service.IsConnected);
        Assert.Equal("session-1", service.SessionId);
        Assert.Equal(42, service.LastExecutionDurationMs);
        Assert.Equal("CONNECT", WireMessage.Parse(connectJson!).Type);
        var input = WireMessage.Parse(inputJson!);
        Assert.Equal("INPUT", input.Type);
        Assert.Equal("hello", input.GetString("prompt"));
        releaseServer.TrySetResult();
        await server.Completion;
    }

    [Fact]
    public async Task SendInputAsync_ServerPing_RespondsWithPongBeforeOutput()
    {
        string? pong = null;
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"PING\"}", ct);
            pong = await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"pong handled\"}", ct);
        });
        await using var service = Service(server);

        Assert.Equal("pong handled", await service.SendInputAsync("hello", null));
        Assert.Equal("PONG", WireMessage.Parse(pong!).Type);
        await server.Completion;
    }

    [Fact]
    public async Task SendInputAsync_OversizedFrame_ClosesWithMessageTooBig()
    {
        WebSocketCloseStatus? closeStatus = null;
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);

            var fragment = new byte[40];
            await socket.SendAsync(fragment, WebSocketMessageType.Text, endOfMessage: false, ct);
            await socket.SendAsync(fragment, WebSocketMessageType.Text, endOfMessage: true, ct);

            var result = await socket.ReceiveAsync(new byte[128], ct);
            closeStatus = result.CloseStatus;
        });
        await using var service = Service(server);
        service.IncomingFrameSizeLimitBytes = 64;

        var error = await Assert.ThrowsAsync<WebSocketException>(
            () => service.SendInputAsync("hello", null));

        Assert.Contains("exceeded 64 bytes", error.Message, StringComparison.Ordinal);
        await server.Completion;
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, closeStatus);
    }

    [Fact]
    public async Task SendInterruptAsync_ActiveTurn_SendsExactFrameOnExistingSocket()
    {
        string? interruptJson = null;
        var inputSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct); // CONNECT
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct); // INPUT
            interruptJson = await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(
                socket, "{\"type\":\"OUTPUT\",\"result\":\"What would you like me to do?\"}", ct);
        });
        await using var service = Service(server);
        service.InputSent += inputSent.SetResult;

        var turn = service.SendInputAsync("start", null);
        await inputSent.Task;
        await service.SendInterruptAsync();

        Assert.Equal("What would you like me to do?", await turn);
        using var interrupt = System.Text.Json.JsonDocument.Parse(interruptJson!);
        var properties = interrupt.RootElement.EnumerateObject().ToList();
        Assert.Single(properties);
        Assert.Equal("type", properties[0].Name);
        Assert.Equal("INTERRUPT", properties[0].Value.GetString());
        await server.Completion;
    }

    [Fact]
    public async Task SendRuntimeInputAsync_ActiveTurn_SendsAnotherInputWithoutReplacingPendingOutput()
    {
        string? runtimeInputJson = null;
        var inputSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct); // CONNECT
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct); // initial INPUT
            runtimeInputJson = await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(
                socket, "{\"type\":\"OUTPUT\",\"result\":\"updated result\"}", ct);
        });
        await using var service = Service(server);
        service.InputSent += inputSent.SetResult;

        var originalTurn = service.SendInputAsync("start", null);
        await inputSent.Task;
        await service.SendRuntimeInputAsync("change direction");

        Assert.Equal("updated result", await originalTurn);
        var runtimeInput = WireMessage.Parse(runtimeInputJson!);
        Assert.Equal("INPUT", runtimeInput.Type);
        Assert.Equal("change direction", runtimeInput.GetString("prompt"));
        Assert.False(string.IsNullOrWhiteSpace(runtimeInput.GetString("input_id")));
        await server.Completion;
    }

    [Fact]
    public async Task SendInputAsync_AskUser_RaisesRequestAndSendsAnswer()
    {
        string? responseJson = null;
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"ask_user\",\"id\":\"question-1\",\"text\":\"Continue?\",\"options\":[\"Yes\",\"No\"]}", ct);
            responseJson = await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"continued\"}", ct);
        });
        await using var service = Service(server);
        service.AskUserRequested += request => _ = service.RespondAskUserAsync("Yes");

        Assert.Equal("continued", await service.SendInputAsync("start", null));
        var response = WireMessage.Parse(responseJson!);
        Assert.Equal("", response.Type);
        Assert.Equal("Yes", response.GetString("answer"));
        await server.Completion;
    }

    [Fact]
    public async Task SendInterruptAsync_RepeatedRequests_SendOneFrameEach()
    {
        var frames = new List<string>();
        var inputSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            frames.Add(await FakeAgentServer.ReceiveTextAsync(socket, ct));
            frames.Add(await FakeAgentServer.ReceiveTextAsync(socket, ct));
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"stopped\"}", ct);
        });
        await using var service = Service(server);
        service.InputSent += inputSent.SetResult;

        var turn = service.SendInputAsync("start", null);
        await inputSent.Task;
        await service.SendInterruptAsync();
        await service.SendInterruptAsync();

        Assert.Equal("stopped", await turn);
        Assert.Equal(2, frames.Count);
        Assert.All(frames, frame => Assert.Equal("INTERRUPT", WireMessage.Parse(frame).Type));
        await server.Completion;
    }

    [Fact]
    public async Task SendInputAsync_PlanReview_SendsDocumentedMessageResponse()
    {
        string? responseJson = null;
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket,
                "{\"type\":\"plan_review\",\"plan_content\":\"1. Inspect\"}", ct);
            responseJson = await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"revised\"}", ct);
        });
        await using var service = Service(server);
        service.PlanReviewRequested += request => _ = service.RespondPlanReviewAsync("Change the port");

        Assert.Equal("revised", await service.SendInputAsync("start", null));
        var response = WireMessage.Parse(responseJson!);
        Assert.Equal("", response.Type);
        Assert.Equal("Change the port", response.GetString("message"));
        await server.Completion;
    }

    [Fact]
    public async Task SendInputAsync_DiffPreview_IsNotificationAndSendsNoResponse()
    {
        var streamed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket,
                "{\"type\":\"diff_preview\",\"path\":\"a.txt\",\"preview\":\"@@ -1 +1 @@\\n-old\\n+new\"}", ct);
            await streamed.Task.WaitAsync(ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"done\"}", ct);
        });
        await using var service = Service(server);
        service.StreamEvent += value =>
        {
            if (value.Type == "diff_preview") streamed.TrySetResult();
        };

        Assert.Equal("done", await service.SendInputAsync("start", null));
        await server.Completion;
    }

    [Fact]
    public async Task SendInputAsync_SessionSync_BuffersOnlyScalarSessionState()
    {
        var observed = new TaskCompletionSource<AgentStreamEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oversizedHistory = new string('x', 200_000);
        var sessionSync = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "session_sync",
            id = "sync-1",
            ts = 1_783_153_058.5,
            session_id = "session-1",
            session = new
            {
                session_id = "session-1",
                mode = "plan",
                turn = 7,
                iteration = 3,
                messages = new[] { new { role = "assistant", content = oversizedHistory } },
                trace = new[] { new { type = "tool_result", result = oversizedHistory } },
                permissions = new { read = new { allowed = true, reason = oversizedHistory } },
            },
        });

        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, sessionSync, ct);
            await observed.Task.WaitAsync(ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"done\"}", ct);
        });
        await using var service = Service(server);
        service.StreamEvent += value =>
        {
            if (value.Type == "session_sync") observed.TrySetResult(value);
        };

        Assert.Equal("done", await service.SendInputAsync("start", "session-1"));
        var streamed = await observed.Task;
        Assert.Equal("sync-1", streamed.EventId);
        Assert.True(streamed.RawJson.Length < 512);
        Assert.DoesNotContain(oversizedHistory, streamed.RawJson, StringComparison.Ordinal);

        using var payload = System.Text.Json.JsonDocument.Parse(streamed.RawJson);
        var root = payload.RootElement;
        Assert.Equal("session_sync", root.GetProperty("type").GetString());
        Assert.Equal("session-1", root.GetProperty("session_id").GetString());
        var session = root.GetProperty("session");
        Assert.Equal("plan", session.GetProperty("mode").GetString());
        Assert.Equal(7, session.GetProperty("turn").GetInt64());
        Assert.Equal(3, session.GetProperty("iteration").GetInt64());
        Assert.False(session.TryGetProperty("messages", out _));
        Assert.False(session.TryGetProperty("trace", out _));
        Assert.False(session.TryGetProperty("permissions", out _));
        await server.Completion;
    }

    [Fact]
    public async Task SendInputAsync_HandshakeError_ThrowsComprehensibleException()
    {
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"ERROR\",\"message\":\"not authorized\"}", ct);
        });
        await using var service = Service(server);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendInputAsync("hello", null));

        Assert.Contains("not authorized", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(service.IsConnected);
        await server.Completion;
    }

    [Fact]
    public async Task SendInputAsync_ServerNeverAuthenticates_ThrowsConnectTimeout()
    {
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        });
        await using var service = Service(
            server,
            connectTimeout: TimeSpan.FromMilliseconds(100),
            silenceTimeout: TimeSpan.FromSeconds(5),
            watchdogInterval: TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<Exception>(() => service.SendInputAsync("hello", null));

        Assert.False(service.IsConnected);
    }

    [Fact]
    public async Task SendInputAsync_AuthenticatedServerGoesSilent_RaisesWatchdogTimeout()
    {
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        });
        await using var service = Service(
            server,
            connectTimeout: TimeSpan.FromSeconds(1),
            silenceTimeout: TimeSpan.FromMilliseconds(80),
            watchdogInterval: TimeSpan.FromMilliseconds(20));
        // Awaited, not sampled. ConnectionLost is raised by the watchdog on its own timer thread,
        // and nothing orders that against SendInputAsync's throw — so reading a plain captured
        // field straight after the await is a race that reports "Value is null" roughly one run in
        // five. Waiting for the event makes the assertion mean "the watchdog reported the loss",
        // which is what it was always trying to say.
        var connectionLoss = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ConnectionLost += error => connectionLoss.TrySetResult(error);

        var error = await Assert.ThrowsAsync<TimeoutException>(() => service.SendInputAsync("hello", null));

        Assert.Equal("Connection went silent", error.Message);
        var reported = await connectionLoss.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<TimeoutException>(reported);
        Assert.False(service.IsConnected);
    }

    [Fact]
    public async Task SendInputAsync_AskUserWaitsPastSilenceTimeout_WatchdogStaysConnected()
    {
        var questionRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"ask_user\",\"id\":\"question-1\",\"text\":\"Continue?\"}", ct);
            var response = WireMessage.Parse(await FakeAgentServer.ReceiveTextAsync(socket, ct));
            Assert.Equal("", response.Type);
            await Task.Delay(TimeSpan.FromMilliseconds(75), ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"continued\"}", ct);
        });
        await using var service = Service(
            server,
            connectTimeout: TimeSpan.FromSeconds(1),
            silenceTimeout: TimeSpan.FromMilliseconds(250),
            watchdogInterval: TimeSpan.FromMilliseconds(25));
        Exception? connectionLoss = null;
        service.ConnectionLost += error => connectionLoss = error;
        service.AskUserRequested += _ => questionRaised.TrySetResult();
        var send = service.SendInputAsync("start", null);

        await questionRaised.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(750);
        Assert.True(service.IsConnected);
        Assert.Null(connectionLoss);
        await service.RespondAskUserAsync("Yes");

        Assert.Equal("continued", await send);
    }

    [Fact]
    public async Task SendInputAsync_OnboardWaitsPastSilenceTimeout_WatchdogStaysConnected()
    {
        var onboardRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"ONBOARD_REQUIRED\"}", ct);
            var submit = WireMessage.Parse(await FakeAgentServer.ReceiveTextAsync(socket, ct));
            Assert.Equal("ONBOARD_SUBMIT", submit.Type);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"ONBOARD_SUCCESS\"}", ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"onboarded\"}", ct);
        });
        await using var service = Service(
            server,
            connectTimeout: TimeSpan.FromSeconds(1),
            silenceTimeout: TimeSpan.FromMilliseconds(250),
            watchdogInterval: TimeSpan.FromMilliseconds(25));
        Exception? connectionLoss = null;
        service.ConnectionLost += error => connectionLoss = error;
        service.OnboardRequired += _ => onboardRaised.TrySetResult();
        var send = service.SendInputAsync("start", null);

        await onboardRaised.Task.WaitAsync(TimeSpan.FromSeconds(1));
        // A human typing an invite code takes as long as it takes — the socket must survive it.
        await Task.Delay(750);
        Assert.Null(connectionLoss);
        await service.SubmitOnboardInviteCodeAsync("invite-123");

        Assert.Equal("onboarded", await send);
    }

    [Fact]
    public async Task SendInputAsync_WithMode_SendsModeChangeOnTheFrameAfterInput()
    {
        string? connectJson = null;
        string? inputJson = null;
        string? modeJson = null;
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            connectJson = await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\",\"session_id\":\"s1\"}", ct);
            inputJson = await FakeAgentServer.ReceiveTextAsync(socket, ct);
            modeJson = await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"ok\"}", ct);
        });
        await using var service = Service(server);

        await service.SendInputAsync("hello", "s1", mode: AgentModes.Plan);

        // Order is the whole point: the host only hands a mode_change to a *running* agent, so it
        // has to arrive behind the INPUT that spawns one. Sent first, it would be dropped.
        Assert.Equal("INPUT", WireMessage.Parse(inputJson!).Type);
        var mode = WireMessage.Parse(modeJson!);
        Assert.Equal("mode_change", mode.Type);
        Assert.Equal("plan", mode.GetString("mode"));

        // And it rides on the CONNECT too, which is the only thing that can set the mode of a
        // session the host has never run before (its session merge discards ours after that).
        var connect = WireMessage.Parse(connectJson!);
        Assert.True(connect.TryGet("session", out var session));
        Assert.Equal("plan", session.GetProperty("mode").GetString());
        await server.Completion;
    }

    [Fact]
    public async Task RespondApprovalAsync_Rejected_DefaultsToRejectHardSoTheAgentActuallyStops()
    {
        string? responseJson = null;
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(
                socket, "{\"type\":\"approval_needed\",\"tool\":\"bash\",\"arguments\":{}}", ct);
            responseJson = await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"stopped\"}", ct);
        });
        await using var service = Service(server);
        service.ApprovalRequested += request => _ = service.RespondApprovalAsync(approved: false, feedback: "no");

        Assert.Equal("stopped", await service.SendInputAsync("start", null));

        // reject_soft only skips the one tool and lets the loop continue; reject_hard is what sets
        // the host's stop_signal and ends the turn. A user who clicked Reject meant the latter.
        var response = WireMessage.Parse(responseJson!);
        Assert.Equal("", response.Type);
        Assert.False(response.GetBool("approved"));
        Assert.Equal(ApprovalRejectModes.Hard, response.GetString("mode"));
        Assert.Equal("no", response.GetString("feedback"));
        Assert.False(response.TryGet("scope", out _));
        await server.Completion;
    }

    [Theory]
    [InlineData(ApprovalRejectModes.Soft)]
    [InlineData(ApprovalRejectModes.Hard)]
    [InlineData(ApprovalRejectModes.Explain)]
    public async Task RespondApprovalAsync_RejectionModes_SendFeedbackWithoutTypeOrScope(string mode)
    {
        string? responseJson = null;
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(
                socket, "{\"type\":\"approval_needed\",\"tool\":\"bash\",\"arguments\":{}}", ct);
            responseJson = await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"done\"}", ct);
        });
        await using var service = Service(server);
        service.ApprovalRequested += request => _ = service.RespondApprovalAsync(
            approved: false, rejectMode: mode, feedback: "user feedback");

        Assert.Equal("done", await service.SendInputAsync("start", null));

        using var response = System.Text.Json.JsonDocument.Parse(responseJson!);
        var root = response.RootElement;
        Assert.False(root.GetProperty("approved").GetBoolean());
        Assert.Equal(mode, root.GetProperty("mode").GetString());
        Assert.Equal("user feedback", root.GetProperty("feedback").GetString());
        Assert.False(root.TryGetProperty("type", out _));
        Assert.False(root.TryGetProperty("scope", out _));
        await server.Completion;
    }

    [Fact]
    public async Task ModeChanged_AgentEntersPlanModeItself_RaisesEventAndStreamsIt()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(
                socket,
                "{\"type\":\"mode_changed\",\"id\":\"e1\",\"mode\":\"plan\",\"triggered_by\":\"agent\"}",
                ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"done\"}", ct);
            await release.Task.WaitAsync(ct);
        });
        await using var service = Service(server);
        ModeChangedEvent? observed = null;
        var streamed = new List<string>();
        service.ModeChanged += e => observed = e;
        service.StreamEvent += e => streamed.Add(e.Type);

        await service.SendInputAsync("hello", null);

        // The agent can switch itself into plan mode; the client has to follow it rather than keep
        // asserting whatever the user last picked — hence the typed event *and* the stream copy the
        // projection turns into a visible card.
        Assert.Equal("plan", observed?.Mode);
        Assert.Equal("agent", observed?.TriggeredBy);
        Assert.Contains("mode_changed", streamed);
        release.TrySetResult();
        await server.Completion;
    }

    [Fact]
    public async Task QuerySessionStatusAsync_HostReportsRunning_ReturnsRunning()
    {
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"done\"}", ct);
            var query = WireMessage.Parse(await FakeAgentServer.ReceiveTextAsync(socket, ct));
            Assert.Equal("SESSION_STATUS", query.Type);
            await FakeAgentServer.SendTextAsync(
                socket, "{\"type\":\"SESSION_STATUS\",\"session_id\":\"s1\",\"status\":\"running\"}", ct);
        });
        await using var service = Service(server);
        await service.SendInputAsync("hello", "s1");

        Assert.Equal(SessionStatuses.Running, await service.QuerySessionStatusAsync("s1"));
        await server.Completion;
    }

    [Fact]
    public async Task QuerySessionStatusAsync_NoConnection_ReportsNotFoundRatherThanThrowing()
    {
        await using var service = new AgentConnectionService(
            "0x1", "http://127.0.0.1:1", AgentIdentity.Generate());

        Assert.Equal(SessionStatuses.NotFound, await service.QuerySessionStatusAsync("s1"));
    }

    [Fact]
    public async Task ReconnectAsync_PreservesStreamAndTypedEventSubscribers()
    {
        var connectionNumber = 0;
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            var current = Interlocked.Increment(ref connectionNumber);
            await FakeAgentServer.ReceiveTextAsync(socket, ct); // CONNECT
            await FakeAgentServer.SendTextAsync(
                socket, "{\"type\":\"CONNECTED\",\"session_id\":\"session-1\"}", ct);

            var input = WireMessage.Parse(await FakeAgentServer.ReceiveTextAsync(socket, ct));
            Assert.Equal("INPUT", input.Type);
            if (current == 2)
            {
                await FakeAgentServer.SendTextAsync(
                    socket,
                    "{\"type\":\"mode_changed\",\"id\":\"mode-2\",\"mode\":\"plan\",\"triggered_by\":\"agent\"}",
                    ct);
            }
            await FakeAgentServer.SendTextAsync(
                socket, $"{{\"type\":\"OUTPUT\",\"result\":\"turn-{current}\"}}", ct);

            try { await FakeAgentServer.ReceiveTextAsync(socket, ct); }
            catch (WebSocketException) { }
        });
        await using var service = Service(server);
        var streamed = new List<string>();
        ModeChangedEvent? modeChanged = null;
        service.StreamEvent += value => streamed.Add(value.Type);
        service.ModeChanged += value => modeChanged = value;

        Assert.Equal("turn-1", await service.SendInputAsync("first", "session-1"));
        await service.ReconnectAsync("session-1");
        Assert.Equal("turn-2", await service.SendInputAsync("second", "session-1"));

        Assert.Contains("mode_changed", streamed);
        Assert.Equal(AgentModes.Plan, modeChanged?.Mode);
        Assert.Equal(2, Volatile.Read(ref connectionNumber));
    }

    [Fact]
    public async Task SendInputAsync_ConnectionDropsDuringTurn_AutomaticallyReconnectsAndResumes()
    {
        var connectionNumber = 0;
        string? reconnectJson = null;
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            var current = Interlocked.Increment(ref connectionNumber);
            var connect = await FakeAgentServer.ReceiveTextAsync(socket, ct);
            if (current == 1)
            {
                await FakeAgentServer.SendTextAsync(
                    socket,
                    "{\"type\":\"CONNECTED\",\"session_id\":\"session-1\",\"status\":\"connected\"}",
                    ct);
                await FakeAgentServer.ReceiveTextAsync(socket, ct); // INPUT
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.InternalServerError,
                    "test drop",
                    ct);
                return;
            }

            reconnectJson = connect;
            await FakeAgentServer.SendTextAsync(
                socket,
                "{\"type\":\"CONNECTED\",\"session_id\":\"session-1\",\"status\":\"running\"}",
                ct);
            await FakeAgentServer.SendTextAsync(
                socket,
                "{\"type\":\"OUTPUT\",\"result\":\"resumed\"}",
                ct);
        });
        await using var service = Service(server);
        var reconnecting = new TaskCompletionSource<ReconnectingEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.Reconnecting += value => reconnecting.TrySetResult(value);

        var result = await service.SendInputAsync("hello", "session-1")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("resumed", result);
        Assert.Equal(1, (await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(1))).Attempt);
        Assert.Equal(2, Volatile.Read(ref connectionNumber));
        Assert.NotNull(reconnectJson);
        var reconnect = WireMessage.Parse(reconnectJson);
        Assert.Equal("session-1", reconnect.GetString("session_id"));
    }

    [Fact]
    public async Task DisposeAsync_ConnectedService_DoesNotRaiseConnectionLost()
    {
        await using var server = new FakeAgentServer(async (socket, ct) =>
        {
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"CONNECTED\"}", ct);
            await FakeAgentServer.ReceiveTextAsync(socket, ct);
            await FakeAgentServer.SendTextAsync(socket, "{\"type\":\"OUTPUT\",\"result\":\"done\"}", ct);
            try { await FakeAgentServer.ReceiveTextAsync(socket, ct); }
            catch (WebSocketException) { }
        });
        var service = Service(server);
        Exception? connectionLoss = null;
        service.ConnectionLost += error => connectionLoss = error;
        await service.SendInputAsync("hello", null);

        await service.DisposeAsync();

        Assert.Null(connectionLoss);
        Assert.False(service.IsConnected);
    }

    [Fact]
    public async Task DisposeAsync_CalledRepeatedly_DoesNotThrow()
    {
        var service = new AgentConnectionService("0x1", "http://127.0.0.1:1", AgentIdentity.Generate());

        await service.DisposeAsync();
        await service.DisposeAsync();
        await service.DisposeAsync();
    }

    [Fact]
    public async Task SendInputAsync_UnreachableEndpoint_FailsWithoutConnectionLostEvent()
    {
        var port = ReserveClosedPort();
        await using var service = new AgentConnectionService(
            "0x1", $"http://127.0.0.1:{port}", AgentIdentity.Generate());
        var raised = false;
        service.ConnectionLost += _ => raised = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<Exception>(() => service.SendInputAsync("hello", null, ct: timeout.Token));

        Assert.False(service.IsConnected);
        Assert.False(raised);
    }

    private static AgentConnectionService Service(
        FakeAgentServer server,
        TimeSpan? connectTimeout = null,
        TimeSpan? silenceTimeout = null,
        TimeSpan? watchdogInterval = null) =>
        new(
            "0x1",
            server.BaseUri.ToString(),
            AgentIdentity.Generate(),
            connectTimeout: connectTimeout,
            silenceTimeout: silenceTimeout,
            watchdogInterval: watchdogInterval);

    private static int ReserveClosedPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
