using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.TrimSmoke;

/// <summary>
/// The regression this project exists for. A trimmed Release build restored Tool Activity cards
/// as empty because the <c>event_args</c> column round-trip went through the reflection
/// serializer; Debug was fine, so nothing caught it until a human opened an old conversation.
///
/// <para>Split into <see cref="WriteAsync"/> and <see cref="VerifyAsync"/> so the runner can do
/// the two halves in <b>separate processes</b> against one data root (<c>persist</c> then
/// <c>verify</c>). Restarting for real is the only way to prove the row survived rather than the
/// in-memory object having been handed back — which is exactly the distinction the original bug
/// turned on.</para>
/// </summary>
internal static class PersistenceChecks
{
    private const string ConversationId = "trim-smoke-conversation";

    public static async Task WriteAsync(Harness h)
    {
        h.Section("Persisting a turn");

        await h.CheckAsync("schema initializes", async () =>
        {
            await using var connection = await AppDatabase.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO agents (id, name, address)
                VALUES ('trim-smoke-agent', 'Trim Smoke', '0xtrimsmoke');
                INSERT OR IGNORE INTO sessions (id, agent_id, title, created_at, updated_at)
                VALUES ($id, 'trim-smoke-agent', 'Trim smoke', '2026-01-01', '2026-01-01');
                """;
            command.Parameters.AddWithValue("$id", ConversationId);
            await command.ExecuteNonQueryAsync();
        });

        await h.CheckAsync("a turn's messages persist", async () =>
        {
            var repository = new ConversationRepository();
            await repository.UpsertMessagesAsync(ConversationId, BuildTurn());
        });

        await h.CheckAsync("preferences persist", async () =>
        {
            var repository = new PreferencesRepository();
            var snapshot = await repository.LoadAsync();
            snapshot.ShortcutOverrides["file.newConversation"] = "Ctrl+Shift+N";
            await repository.SaveAsync(snapshot);
        });
    }

    public static async Task VerifyAsync(Harness h)
    {
        h.Section("Restoring the turn");

        var repository = new ConversationRepository();
        var messages = await repository.LoadMessagesAsync(ConversationId);

        // Four, not the five that went in: a settled approval bubble is filtered out of every
        // read on purpose (ConversationRepository's `event_kind IS NOT 'approval'`), because the
        // decision is shown inside the tool-activity card it belongs to.
        h.Check("the turn's messages came back", () =>
            Harness.Equal(4, messages.Count, "message count changed across the restart"));

        h.Check("tool activity survives with its steps, arguments and results", () =>
        {
            var card = messages.FirstOrDefault(m => m.EventKind == "tool_activity");
            Harness.NotNull(card, "the tool_activity row did not come back at all");
            Harness.NotNull(card!.ToolActivity,
                "tool_activity row restored with a null ToolActivity — this is the exact trimmed-build "
                + "failure the release audit found");

            var steps = card.ToolActivity!.Steps;
            Harness.Equal(2, steps.Count, "tool steps were lost");
            Harness.Equal("bash", steps[0].ToolName, "tool name lost");
            Harness.Equal(ToolStepStatus.Success, steps[0].Status, "step status lost");
            Harness.Contains("ls -la", steps[0].Arguments ?? "", "step arguments lost");
            Harness.Contains("total 8", steps[0].Result ?? "", "step result lost");
            Harness.Equal(ToolStepStatus.Failed, steps[1].Status, "failed step status lost");
            Harness.Equal(ToolActivityStatus.PartialSuccess, card.ToolActivity.Status,
                "card status lost");

            // Derived at render time from the persisted arguments rather than stored, so this also
            // proves the derivation still runs against a restored row.
            Harness.NotNull(steps[0].Invocation, "tool invocation was not derived from restored arguments");
        });

        h.Check("every readable interactive card survives with its resolved answer", () =>
        {
            foreach (var (kind, expectedMeta) in new[]
            {
                ("ask_user", "region=apac"),
                ("plan_review", "Plan approved"),
            })
            {
                var card = messages.FirstOrDefault(m => m.EventKind == kind);
                Harness.NotNull(card, $"the {kind} card did not come back");
                Harness.Equal(expectedMeta, card!.EventMeta, $"the {kind} card lost its resolved answer");
                Harness.Equal(EventStatus.Done, card.Status, $"the {kind} card lost its status");
            }
        });

        h.Check("a settled approval bubble stays filtered out of the read", () =>
            Harness.True(messages.All(m => m.EventKind != "approval"),
                "an approval bubble reached the transcript; it belongs inside its tool-activity card"));

        h.Check("attachment metadata survives without its payload", () =>
        {
            var card = messages.First(m => m.Attachments.Count > 0);
            var attachment = card.Attachments[0];
            Harness.Equal("shot.png", attachment.FileName, "attachment filename lost");
            Harness.Equal(AttachmentKind.Image, attachment.Kind, "attachment kind lost");
            Harness.True(!string.IsNullOrEmpty(attachment.LocalCachePath), "cache path lost");
        });

        await h.CheckAsync("preferences survive", async () =>
        {
            var snapshot = await new PreferencesRepository().LoadAsync();
            Harness.Equal("Ctrl+Shift+N",
                snapshot.ShortcutOverrides.GetValueOrDefault("file.newConversation"),
                "the shortcut override map did not round-trip");
        });
    }

    /// <summary>
    /// One turn holding every shape whose persistence goes through JSON metadata: the tool
    /// timeline in <c>event_args</c>, the three interactive cards with the answers
    /// <c>ResolveInteractiveCards</c> stamps on them, and an agent bubble carrying an attachment.
    /// </summary>
    private static IReadOnlyList<ChatMessage> BuildTurn()
    {
        var activity = new ToolActivityViewModel
        {
            TurnId = "turn-1",
            Status = ToolActivityStatus.PartialSuccess,
            Summary = "2 tools",
        };
        activity.Steps.Add(new ToolStepViewModel
        {
            Sequence = 0,
            ToolName = "bash",
            DisplayName = "Run command",
            Status = ToolStepStatus.Success,
            Arguments = """{"command":"ls -la"}""",
            Result = "total 8\ndrwxr-xr-x 2 user user 4096 .",
            DurationMs = 340,
        });
        activity.Steps.Add(new ToolStepViewModel
        {
            Sequence = 1,
            ToolName = "read_file",
            DisplayName = "Read file",
            Status = ToolStepStatus.Failed,
            Arguments = """{"path":"missing.txt"}""",
            Error = "ENOENT",
        });

        return
        [
            new ChatMessage { Id = 1, Role = ChatRole.User, Content = "run the checks" },
            new ChatMessage
            {
                Id = 2,
                Role = ChatRole.Agent,
                EventKind = "tool_activity",
                ToolActivity = activity,
            },
            new ChatMessage
            {
                Id = 3,
                Role = ChatRole.Agent,
                EventKind = "ask_user",
                EventTitle = "Which region?",
                EventMeta = "region=apac",
                Status = EventStatus.Done,
            },
            new ChatMessage
            {
                Id = 4,
                Role = ChatRole.Agent,
                EventKind = "approval",
                EventTitle = "Run deploy.sh?",
                EventMeta = "Approved once",
                Status = EventStatus.Done,
            },
            new ChatMessage
            {
                Id = 5,
                Role = ChatRole.Agent,
                EventKind = "plan_review",
                EventTitle = "Review the plan",
                EventMeta = "Plan approved",
                Status = EventStatus.Done,
                Attachments =
                {
                    new ChatAttachment
                    {
                        Id = "sha256-abc",
                        Kind = AttachmentKind.Image,
                        FileName = "shot.png",
                        MimeType = "image/png",
                        LocalCachePath = Path.Combine(AppStorage.ImageCacheDir, "sha256-abc.png"),
                    },
                },
            },
        ];
    }
}
