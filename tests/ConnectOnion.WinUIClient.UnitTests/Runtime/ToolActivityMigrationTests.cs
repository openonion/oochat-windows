using System.Globalization;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

public sealed class ToolActivityMigrationTests
{
    [Fact]
    public void UpgradeLegacyToolMessages_ConsecutiveRows_CollapsesIntoTimeline()
    {
        var source = new[]
        {
            Legacy(1, "read_file", EventStatus.Done),
            Legacy(2, "run_command", EventStatus.Error),
        };

        var upgraded = ToolActivityMigration.UpgradeLegacyToolMessages(source);

        var message = Assert.Single(upgraded);
        Assert.Equal("tool_activity", message.EventKind);
        Assert.Equal(2, message.ToolActivity!.Steps.Count);
        Assert.Equal(ToolStepStatus.Failed, message.ToolActivity.Steps[1].Status);
        Assert.Equal(ToolActivityStatus.PartialSuccess, message.ToolActivity.Status);
    }

    [Fact]
    public void UpgradeLegacyToolMessages_NonToolRows_PreservesOriginalInstanceAndOrder()
    {
        var user = new ChatMessage { Id = 1, Role = ChatRole.User, Content = "hello" };
        var agent = new ChatMessage { Id = 3, Role = ChatRole.Agent, Content = "done" };
        var source = new[] { user, Legacy(2, "read_file", EventStatus.Done), agent };

        var upgraded = ToolActivityMigration.UpgradeLegacyToolMessages(source);

        Assert.Equal(3, upgraded.Count);
        Assert.Same(user, upgraded[0]);
        Assert.Equal("tool_activity", upgraded[1].EventKind);
        Assert.Same(agent, upgraded[2]);
    }

    [Fact]
    public void UpgradeLegacyToolMessages_CachedPlanReview_StartsCollapsedAsHistory()
    {
        var plan = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = "plan_review",
            EventTitle = "Review execution plan",
            IsInteractiveCardExpanded = true,
        };

        var restored = Assert.Single(ToolActivityMigration.UpgradeLegacyToolMessages([plan]));

        Assert.Same(plan, restored);
        Assert.False(restored.IsInteractiveCardExpanded);
    }

    [Fact]
    public void UpgradeLegacyToolMessages_RelinksHistoricalDiffAndApproval()
    {
        var diff = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = "diff_preview",
            EventTitle = "/tmp/test.txt",
            EventDetail = "+new",
            Status = EventStatus.Done,
        };
        var approval = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = "ask_user",
            EventTitle = "Apply changes to /tmp/test.txt?",
            EventMeta = "Answered: Yes, apply this change",
            Status = EventStatus.Done,
        };

        var restored = ToolActivityMigration.UpgradeLegacyToolMessages([diff, approval]);

        Assert.Same(diff, approval.RelatedDiffPreview);
        Assert.Same(approval, diff.RelatedDiffApproval);
        Assert.False(diff.ShowRelatedDiffApprovalCard);
        // The decision is settled, so it shows its own folded row rather than being hosted by the
        // diff. This is the reload half of the ownership swap: the pairing has to survive so the
        // restored transcript matches what was on screen while the turn ran.
        Assert.True(approval.IsTranscriptRowVisible);
        Assert.False(approval.IsInteractiveCardExpanded);
        // Unconfirmed is a problem state, so the diff is kept rather than withdrawn.
        Assert.Equal(DiffChangeState.Unconfirmed, diff.DiffState);
        Assert.False(diff.IsWithdrawnDiffPreview);
        Assert.True(diff.IsTranscriptRowVisible);
        Assert.Equal(2, restored.Count);
    }

    // The legacy writer was `ms < 1000 ? $"{ms:0} ms" : $"{ms / 1000:0.0} s"`, so these two
    // shapes are the only ones real data can contain. "340 ms" used to come back as 340000
    // because the seconds check matched the "s" inside "ms".
    [Theory]
    [InlineData("340 ms", 340d)]
    [InlineData("999 ms", 999d)]
    [InlineData("1.5 s", 1500d)]
    [InlineData("12.0 s", 12000d)]
    public void UpgradeLegacyToolMessages_LegacyDurationMeta_ParsesToMilliseconds(string meta, double expectedMs)
    {
        var row = Legacy(1, "read_file", EventStatus.Done);
        row.EventMeta = meta;

        var upgraded = ToolActivityMigration.UpgradeLegacyToolMessages(new[] { row });

        var step = Assert.Single(upgraded[0].ToolActivity!.Steps);
        Assert.Equal(expectedMs, step.DurationMs);
    }

    [Fact]
    public void UpgradeLegacyToolMessages_DecimalMeta_ParsesUnderACommaDecimalCulture()
    {
        // The meta string always used '.' as the decimal point, but parsing used the ambient
        // culture — so under a comma-decimal locale "1.5 s" parsed as 15 seconds.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var row = Legacy(1, "read_file", EventStatus.Done);
            row.EventMeta = "1.5 s";

            var upgraded = ToolActivityMigration.UpgradeLegacyToolMessages(new[] { row });

            Assert.Equal(1500d, Assert.Single(upgraded[0].ToolActivity!.Steps).DurationMs);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void UpgradeLegacyToolMessages_UnparseableMeta_LeavesDurationUnset()
    {
        var row = Legacy(1, "read_file", EventStatus.Done);
        row.EventMeta = "unknown";

        var upgraded = ToolActivityMigration.UpgradeLegacyToolMessages(new[] { row });

        Assert.Null(Assert.Single(upgraded[0].ToolActivity!.Steps).DurationMs);
    }

    [Theory]
    [InlineData(ToolActivityStatus.Running)]
    [InlineData(ToolActivityStatus.WaitingForConfirmation)]
    [InlineData(ToolActivityStatus.WaitingForPermission)]
    public void UpgradeLegacyToolMessages_RestoredNonTerminalActivity_IsSealedAsHistory(
        ToolActivityStatus staleStatus)
    {
        var activity = new ToolActivityViewModel { Status = staleStatus };
        activity.Steps.Add(new ToolStepViewModel
        {
            Id = "done",
            Status = ToolStepStatus.Success,
            ToolName = "read_file",
        });
        var message = new ChatMessage
        {
            Id = 1,
            Role = ChatRole.Event,
            EventKind = "tool_activity",
            Status = EventStatus.Running,
            ToolActivity = activity,
        };

        var upgraded = ToolActivityMigration.UpgradeLegacyToolMessages(new[] { message });

        var restored = Assert.Single(upgraded);
        Assert.Equal(ToolActivityStatus.Success, restored.ToolActivity!.Status);
        Assert.Equal(EventStatus.Done, restored.Status);
        Assert.Equal("Done", restored.ToolActivity.CompletionLabel);
        Assert.False(restored.ToolActivity.IsAwaitingApproval);
    }

    [Fact]
    public void UpgradeLegacyToolMessages_RestoredTerminalActivity_ClearsCachedApprovalAndMessageRunningState()
    {
        var approval = new ChatMessage
        {
            EventKind = "approval",
            Status = EventStatus.Running,
        };
        var activity = new ToolActivityViewModel
        {
            Status = ToolActivityStatus.Success,
            Summary = "Completed · 2 steps",
            Approval = approval,
        };
        activity.Steps.Add(new ToolStepViewModel { Status = ToolStepStatus.Success });
        var message = new ChatMessage
        {
            Role = ChatRole.Event,
            EventKind = "tool_activity",
            Status = EventStatus.Running,
            ToolActivity = activity,
        };

        var restored = Assert.Single(ToolActivityMigration.UpgradeLegacyToolMessages([message]));

        Assert.Null(restored.ToolActivity!.Approval);
        Assert.False(restored.ToolActivity.IsAwaitingApproval);
        Assert.Equal(EventStatus.Done, restored.Status);
        Assert.Equal("Completed · 2 steps", restored.ToolActivity.Summary);
    }

    private static ChatMessage Legacy(long id, string tool, EventStatus status) => new()
    {
        Id = id,
        Role = ChatRole.Event,
        EventKind = "tool",
        EventKey = "legacy-" + id,
        EventTitle = tool,
        EventArgs = "{\"token\":\"should-hide\"}",
        EventResult = status == EventStatus.Error ? "failed" : "ok",
        EventMeta = "1.5s",
        Status = status,
    };
}
