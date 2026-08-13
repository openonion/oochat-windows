using System.Text.Json;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.TrimSmoke;

/// <summary>
/// Everything that turns objects into wire bytes or wire bytes into objects. These are pure
/// functions with no storage behind them, so they run in both harness phases.
/// </summary>
internal static class ProtocolChecks
{
    public static void Run(Harness h)
    {
        h.Section("Protocol frames");

        h.Check("INPUT text-only serializes", () =>
        {
            var json = WireJson.Serialize(InputMessageBuilder.BuildInput("hello", "in-1", null));
            Harness.Equal("""{"type":"INPUT","input_id":"in-1","prompt":"hello"}""", json,
                "text-only INPUT shape changed");
        });

        h.Check("INPUT with images and files serializes", () =>
        {
            var json = WireJson.Serialize(InputMessageBuilder.BuildInput(
                "look", "in-2", "0xabc",
                images: ["data:image/png;base64,iVBORw0KGgo="],
                files: [new OutgoingFileAttachment("a.txt", "data:text/plain;base64,QQ==")]));

            using var doc = JsonDocument.Parse(json);
            Harness.Equal(1, doc.RootElement.GetProperty("images").GetArrayLength(), "images lost");
            Harness.Equal("a.txt", doc.RootElement.GetProperty("files")[0].GetProperty("name").GetString(),
                "file name lost");
            Harness.Equal("0xabc", doc.RootElement.GetProperty("to").GetString(), "relay target lost");
        });

        h.Check("CONNECT envelope serializes with a signed payload", () =>
        {
            var identity = AgentIdentity.Generate();
            var envelope = identity.SignPayload(
            [
                new("timestamp", 1_700_000_000L),
                new("to", "0xagent"),
            ]);

            var json = WireJson.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "CONNECT",
                ["payload"] = envelope.Payload,
                ["from"] = envelope.From,
                ["signature"] = envelope.Signature,
                ["timestamp"] = envelope.Timestamp,
                ["session"] = new Dictionary<string, object?>
                {
                    ["session_id"] = "s-1",
                    ["mode"] = AgentModes.Plan,
                },
            });

            using var doc = JsonDocument.Parse(json);
            Harness.Equal("CONNECT", doc.RootElement.GetProperty("type").GetString(), "type lost");
            Harness.NotNull(doc.RootElement.GetProperty("signature").GetString(), "signature lost");
            Harness.Equal("s-1",
                doc.RootElement.GetProperty("session").GetProperty("session_id").GetString(),
                "session id lost");
        });

        h.Check("control frames serialize", () =>
        {
            Harness.Equal("""{"type":"INTERRUPT"}""",
                WireJson.Serialize(new Dictionary<string, object?> { ["type"] = "INTERRUPT" }),
                "INTERRUPT shape changed");
            Harness.Equal("""{"type":"mode_change","mode":"plan","turns":3}""",
                WireJson.Serialize(new Dictionary<string, object?>
                {
                    ["type"] = "mode_change", ["mode"] = AgentModes.Plan, ["turns"] = 3,
                }),
                "mode_change shape changed");
        });

        h.Check("canonical signing JSON still verifies", () =>
        {
            var identity = AgentIdentity.Generate();
            var envelope = identity.SignPayload([new("timestamp", 1_700_000_000L)]);
            Harness.True(envelope.From.StartsWith("0x", StringComparison.Ordinal), "address not hex");
            Harness.True(envelope.Signature.Length > 0, "signature empty");
        });

        h.Section("Inbound event parsing");

        h.Check("ask_user parses, including a field form", () =>
        {
            var msg = Wire("""
                {"type":"ask_user","id":"q-1","text":"Sign in","multi_select":false,
                 "fields":[{"name":"username","label":"User","required":true},
                           {"name":"password","label":"Password","required":true,"type":"password"}]}
                """);
            var request = AgentInteractiveParsers.ParseAskUser(msg);

            Harness.Equal("q-1", request.Id, "ask_user id lost");
            Harness.Equal(2, request.Fields.Count, "ask_user fields lost");
            Harness.Equal("password", request.Fields[1].Type, "password field type lost");
        });

        h.Check("ONBOARD_REQUIRED parses both gate methods", () =>
        {
            var request = AgentInteractiveParsers.ParseOnboard(Wire("""
                {"type":"ONBOARD_REQUIRED","methods":["invite_code","payment"],
                 "payment_amount":2.5,"payment_address":"0xpay"}
                """));

            Harness.True(request.AcceptsInviteCode, "invite gate lost");
            Harness.True(request.AcceptsPayment, "payment gate lost");
            Harness.Equal("0xpay", request.PaymentAddress, "payment address lost");
        });

        h.Check("agent_image and files_received attachment events parse", () =>
        {
            Harness.True(
                AttachmentWireEvents.TryGetAgentImageDataUrl(Wire("""
                    {"type":"agent_image","image":"data:image/png;base64,iVBORw0KGgo="}
                    """), out var dataUrl),
                "agent_image did not parse");
            Harness.True(DataUrlCodec.TryDecode(dataUrl, out var mime, out _), "data URL did not decode");
            Harness.Equal("image/png", mime, "image mime type lost");

            Harness.True(
                AttachmentWireEvents.TryGetFilesReceived(Wire("""
                    {"type":"files_received","files":[{"name":"a.txt","size":3}]}
                    """), out var files),
                "files_received did not parse");
            Harness.Equal(1, files.Count, "received file list lost");
        });

        h.Section("Agent /info");

        h.Check("composed /info round-trips through its own parsers", () =>
        {
            var json = EndpointResolver.SerializeAgentInfo(new AgentInfo(
                "0xabc", Online: true, Name: "researcher",
                Tools: ["bash"],
                Skills: [new SkillInfo("summarize", "Summarize a page")],
                AcceptedInputs: new AgentAcceptedInputs(
                    Text: true, Images: true, Files: new AgentFileInputs(10, 5))));

            var skills = EndpointResolver.ParseSkillsFromInfoJson(json);
            Harness.Equal(1, skills.Count, "skills lost on the round trip");
            Harness.Equal("summarize", skills[0].Name, "skill name lost");

            var inputs = EndpointResolver.ParseAcceptedInputsFromInfoJson(json);
            Harness.NotNull(inputs, "accepted_inputs lost on the round trip");
            Harness.Equal(10, inputs!.Files!.MaxFileSizeMb, "file size limit lost");
        });

        h.Section("Interactive responses");

        h.Check("ask_user field answer serializes to JSON", () =>
        {
            var message = new ChatMessage { EventKind = "ask_user" };
            message.AskUserFields.Add(new AskUserFieldEntry { Name = "username", Value = "bob" });
            message.AskUserFields.Add(new AskUserFieldEntry { Name = "note", Value = "hi & bye" });

            var answer = InteractiveResponseBuilder.BuildAskUserAnswer(message) as string;
            Harness.NotNull(answer, "field answer was not produced");

            using var doc = JsonDocument.Parse(answer!);
            Harness.Equal("bob", doc.RootElement.GetProperty("username").GetString(), "field value lost");
            Harness.Equal("hi & bye", doc.RootElement.GetProperty("note").GetString(), "escaped value lost");
        });

        h.Check("approval and plan-review frames serialize", () =>
        {
            Harness.Equal("""{"approved":false,"mode":"reject_hard","feedback":"no"}""",
                WireJson.Serialize(new Dictionary<string, object?>
                {
                    ["approved"] = false,
                    ["mode"] = ApprovalRejectModes.Hard,
                    ["feedback"] = "no",
                }),
                "approval rejection shape changed");

            var plan = InteractiveResponseBuilder.BuildPlanReviewResponse(PlanReviewAction.Approve, null);
            Harness.NotNull(plan, "plan review response was not produced");
            Harness.Equal("""{"message":""}""",
                WireJson.Serialize(new Dictionary<string, object?> { ["message"] = plan!.Message }),
                "plan_review shape changed");
        });

        h.Section("Tool argument redaction");

        h.Check("tool arguments sanitize without reflection", () =>
        {
            var sanitized = ToolActivityProjector.SanitizeJson(
                """{"url":"https://a.test/?q=x+y","api_key":"sk-live-1","steps":[{"n":1}]}""");

            Harness.Contains("[hidden]", sanitized, "secret not redacted");
            Harness.Contains("x+y", sanitized, "URL was over-escaped");
            Harness.True(!sanitized.Contains("sk-live-1", StringComparison.Ordinal),
                "secret leaked into the timeline");
        });
    }

    private static WireMessage Wire(string json) => WireMessage.Parse(json);
}
