using ConnectOnion.WinUIClient.Models.Notifications;
using ConnectOnion.WinUIClient.Services.Notifications;

namespace ConnectOnion.WinUIClient.UnitTests.Services.Notifications;

public sealed class NotificationCoordinatorTests
{
    private const string AgentId = "agent-1";
    private const string AgentName = "Helper";
    private const string ConvId = "conversation-1";

    private sealed class Harness
    {
        public RecordingChannel System { get; } = new();
        public RecordingChannel InApp { get; } = new();
        public FakeWindowPresence Presence { get; } = new();
        public FakeSettingsProvider Settings { get; } = new();
        public ManualScheduler Scheduler { get; } = new();
        public RecordingAttentionStore Attention { get; } = new();
        public NotificationCoordinator Coordinator { get; }

        public Harness()
        {
            Coordinator = new NotificationCoordinator(
                System, InApp, Presence, Settings, Scheduler, attentionStore: Attention);
        }
    }

    [Fact]
    public void PublishAgentTurnCompleted_ViewingConversation_SuppressesNotification()
    {
        var h = new Harness();
        h.Presence.IsForeground = true;
        h.Presence.ViewingConversationId = ConvId;

        h.Coordinator.PublishAgentTurnCompleted(AgentId, AgentName, ConvId, "turn-1", "hi", 0, 100);

        Assert.Equal(0, h.System.Count);
        Assert.Equal(0, h.InApp.Count);
        Assert.Empty(h.Attention.Calls);
    }

    [Fact]
    public void PublishAgentTurnCompleted_ForegroundOnAnotherConversation_RoutesInApp()
    {
        var h = new Harness();
        h.Presence.IsForeground = true;
        h.Presence.ViewingConversationId = "other";

        h.Coordinator.PublishAgentTurnCompleted(AgentId, AgentName, ConvId, "turn-1", "hi", 0, 100);

        Assert.Equal(1, h.InApp.Count);
        Assert.Equal(0, h.System.Count);
        Assert.Equal(NotificationType.AgentReply, h.InApp.Last!.Type);
        Assert.Equal((ConvId, false), Assert.Single(h.Attention.Calls));
    }

    [Fact]
    public void PublishAgentTurnCompleted_Background_RoutesToSystem()
    {
        var h = new Harness();
        h.Presence.IsForeground = false;

        h.Coordinator.PublishAgentTurnCompleted(AgentId, AgentName, ConvId, "turn-1", "hi", 0, 100);

        Assert.Equal(1, h.System.Count);
        Assert.Equal(0, h.InApp.Count);
    }

    [Fact]
    public void PublishAgentTurnCompleted_MachineName_UsesFriendlyDisplayName()
    {
        var h = new Harness();
        h.Presence.IsForeground = false;

        h.Coordinator.PublishAgentTurnCompleted(
            AgentId, "remote-admin-agent", ConvId, "turn-1", "hi", 0, 100);

        Assert.Equal("Remote Admin Agent", h.System.Last!.Title);
    }

    [Fact]
    public void PublishAgentTurnCompleted_SameTurnTwice_NotifiesOnce()
    {
        var h = new Harness();
        h.Presence.IsForeground = false;

        h.Coordinator.PublishAgentTurnCompleted(AgentId, AgentName, ConvId, "turn-1", "partial", 0, 100);
        h.Coordinator.PublishAgentTurnCompleted(AgentId, AgentName, ConvId, "turn-1", "final full reply", 0, 100);

        Assert.Equal(1, h.System.Count);
    }

    [Fact]
    public void PublishAgentTurnCompleted_WithToolCalls_ClassifiesAsTaskCompleted()
    {
        var h = new Harness();
        h.Presence.IsForeground = false;

        h.Coordinator.PublishAgentTurnCompleted(AgentId, AgentName, ConvId, "turn-1", "done", toolCallCount: 3, durationMs: 100);

        Assert.Equal(NotificationType.TaskCompleted, h.System.Last!.Type);
    }

    [Fact]
    public void PublishAgentTurnCompleted_LongDuration_ClassifiesAsTaskCompleted()
    {
        var h = new Harness();
        h.Presence.IsForeground = false;

        h.Coordinator.PublishAgentTurnCompleted(AgentId, AgentName, ConvId, "turn-1", "done", toolCallCount: 0, durationMs: 20_000);

        Assert.Equal(NotificationType.TaskCompleted, h.System.Last!.Type);
    }

    [Fact]
    public void PublishApprovalRequired_SameToolAndArguments_DedupesToOneNotification()
    {
        var h = new Harness();
        h.Presence.IsForeground = false;

        h.Coordinator.PublishApprovalRequired(AgentId, AgentName, ConvId, "delete_file", "remove x", "{\"path\":\"x\"}");
        h.Coordinator.PublishApprovalRequired(AgentId, AgentName, ConvId, "delete_file", "remove x", "{\"path\":\"x\"}");

        Assert.Equal(1, h.System.Count);
        Assert.Equal(NotificationType.ApprovalRequired, h.System.Last!.Type);
        Assert.Equal((ConvId, true), Assert.Single(h.Attention.Calls));
    }

    [Fact]
    public void NotifyConnectionRestored_WithinGracePeriod_StaysSilent()
    {
        var h = new Harness();
        h.Presence.IsForeground = false;

        h.Coordinator.NotifyConnectionLost(AgentId, AgentName);
        h.Coordinator.NotifyConnectionRestored(AgentId);
        h.Scheduler.FireAll(); // the grace timer would fire here, but it was cancelled

        Assert.Equal(0, h.System.Count);
    }

    [Fact]
    public void NotifyConnectionLost_GracePeriodElapses_Notifies()
    {
        var h = new Harness();
        h.Presence.IsForeground = false;

        h.Coordinator.NotifyConnectionLost(AgentId, AgentName);
        h.Scheduler.FireAll();

        Assert.Equal(1, h.System.Count);
        Assert.Equal(NotificationType.ConnectionLost, h.System.Last!.Type);
    }

    [Fact]
    public void NotifyConnectionLost_MachineName_UsesFriendlyDisplayName()
    {
        var h = new Harness();
        h.Presence.IsForeground = false;

        h.Coordinator.NotifyConnectionLost(AgentId, "remote-admin-agent");
        h.Scheduler.FireAll();

        Assert.Contains("Remote Admin Agent", h.System.Last!.Body);
    }

    [Fact]
    public void NotifyConnectionLost_RepeatedForSameAgent_ArmsGraceTimerOnce()
    {
        var h = new Harness();

        h.Coordinator.NotifyConnectionLost(AgentId, AgentName);
        h.Coordinator.NotifyConnectionLost(AgentId, AgentName);

        Assert.Equal(1, h.Scheduler.PendingCount);
    }

    [Fact]
    public void Dispose_CancelsPendingTimers_AndIgnoresLaterPublishes()
    {
        var h = new Harness();
        h.Presence.IsForeground = false;
        h.Coordinator.NotifyConnectionLost(AgentId, AgentName);

        h.Coordinator.Dispose();
        h.Scheduler.FireAll();
        h.Coordinator.PublishAgentTurnCompleted(
            AgentId, AgentName, ConvId, "turn-after-dispose", "done", 0, 100);

        Assert.Equal(0, h.Scheduler.PendingCount);
        Assert.Equal(0, h.System.Count);
        Assert.Equal(0, h.InApp.Count);
        Assert.Empty(h.Attention.Calls);
    }

    /// <summary>
    /// Turning notifications off silences the toast and nothing else. It used to also stop the
    /// conversation being marked unread, because the persist ran after the channel check — so a
    /// user who did not want to be interrupted also lost the sidebar badge that was the only other
    /// way to find out a reply had arrived.
    /// </summary>
    [Fact]
    public void PublishAgentTurnCompleted_NotificationsDisabled_SuppressesToastButStillMarksUnread()
    {
        var h = new Harness();
        h.Settings.Current = new NotificationSettings { EnableNotifications = false };
        h.Presence.IsForeground = false;

        h.Coordinator.PublishAgentTurnCompleted(AgentId, AgentName, ConvId, "turn-1", "hi", 0, 100);

        Assert.Equal(0, h.System.Count);
        Assert.Equal(0, h.InApp.Count);
        Assert.Equal([(ConvId, false)], h.Attention.Calls);
    }

    /// <summary>Same split for the per-type switches, which are the ones a user actually reaches
    /// for when only one kind of notification is bothering them.</summary>
    [Fact]
    public void PublishAgentTurnCompleted_TypeDisabled_SuppressesToastButStillMarksUnread()
    {
        var h = new Harness();
        h.Settings.Current = new NotificationSettings { NotifyAgentReplies = false };
        h.Presence.IsForeground = false;

        h.Coordinator.PublishAgentTurnCompleted(AgentId, AgentName, ConvId, "turn-1", "hi", 0, 100);

        Assert.Equal(0, h.System.Count);
        Assert.Equal(0, h.InApp.Count);
        Assert.Equal([(ConvId, false)], h.Attention.Calls);
    }

    /// <summary>The one suppression that really does mean "nothing to record": the reply landed in
    /// the conversation already on screen, so there is no unread message to badge. Pinned here
    /// because it is what stops the two changes above from becoming "always mark unread".</summary>
    [Fact]
    public void PublishAgentTurnCompleted_ViewingConversation_MarksNothingUnread()
    {
        var h = new Harness();
        h.Presence.IsForeground = true;
        h.Presence.ViewingConversationId = ConvId;

        h.Coordinator.PublishAgentTurnCompleted(AgentId, AgentName, ConvId, "turn-1", "hi", 0, 100);

        Assert.Equal(0, h.System.Count);
        Assert.Equal(0, h.InApp.Count);
        Assert.Empty(h.Attention.Calls);
    }

    /// <summary>Viewing wins over a disabled switch, which is why the presence check runs first.
    /// Reversed, a user with notifications off would collect unread counts for the very
    /// conversation they were reading, and nothing on that page clears a badge it never earned.
    /// </summary>
    [Fact]
    public void PublishAgentTurnCompleted_ViewingConversationWithNotificationsDisabled_MarksNothingUnread()
    {
        var h = new Harness();
        h.Settings.Current = new NotificationSettings { EnableNotifications = false };
        h.Presence.IsForeground = true;
        h.Presence.ViewingConversationId = ConvId;

        h.Coordinator.PublishAgentTurnCompleted(AgentId, AgentName, ConvId, "turn-1", "hi", 0, 100);

        Assert.Empty(h.Attention.Calls);
    }

    [Fact]
    public void PublishAgentTurnCompleted_MessagePreviewDisabled_OmitsContentFromBody()
    {
        var h = new Harness();
        h.Settings.Current = new NotificationSettings { ShowMessagePreview = false };
        h.Presence.IsForeground = false;

        h.Coordinator.PublishAgentTurnCompleted(AgentId, AgentName, ConvId, "turn-1", "secret content here", 0, 100);

        Assert.Equal(1, h.System.Count);
        Assert.DoesNotContain("secret", h.System.Last!.Body);
    }

    [Fact]
    public void PublishAgentTurnCompleted_ChannelThrows_DoesNotBubbleToCaller()
    {
        var coordinator = new NotificationCoordinator(
            new ThrowingChannel(),
            new RecordingChannel(),
            new FakeWindowPresence { IsForeground = false },
            new FakeSettingsProvider(),
            new ManualScheduler());

        var ex = Record.Exception(() =>
            coordinator.PublishAgentTurnCompleted(AgentId, AgentName, ConvId, "turn-1", "hi", 0, 100));

        Assert.Null(ex);
    }

    private sealed class ThrowingChannel : INotificationChannel
    {
        public void Show(NotificationRequest request, bool playSound)
            => throw new InvalidOperationException("boom");
    }

    private sealed class RecordingAttentionStore : IConversationAttentionStore
    {
        public List<(string ConversationId, bool RequiresAttention)> Calls { get; } = [];

        public Task MarkUnreadAsync(
            string conversationId,
            bool requiresAttention,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((conversationId, requiresAttention));
            return Task.CompletedTask;
        }
    }
}
