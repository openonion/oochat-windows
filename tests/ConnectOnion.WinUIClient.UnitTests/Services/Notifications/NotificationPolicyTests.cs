using ConnectOnion.WinUIClient.Models.Notifications;
using ConnectOnion.WinUIClient.Services.Notifications;

namespace ConnectOnion.WinUIClient.UnitTests.Services.Notifications;

public sealed class NotificationPolicyTests
{
    private readonly NotificationPolicy _policy = new();

    [Fact]
    public void Decide_NotificationsDisabled_SuppressesNotification()
    {
        var decision = _policy.Decide(Request(), new FakePresence(), Settings(enable: false));

        Assert.Equal(NotificationChannel.None, decision.Channel);
        Assert.Equal("notifications disabled", decision.Reason);
    }

    [Fact]
    public void Decide_NotificationTypeDisabled_SuppressesNotification()
    {
        var settings = Settings();
        settings.NotifyAgentReplies = false;

        var decision = _policy.Decide(Request(), new FakePresence(), settings);

        Assert.Equal(NotificationChannel.None, decision.Channel);
        Assert.Equal("type disabled", decision.Reason);
    }

    [Fact]
    public void Decide_ViewingTargetConversation_SuppressesNotification()
    {
        var presence = new FakePresence { ViewedConversationId = "conversation-1" };

        var decision = _policy.Decide(Request(), presence, Settings());

        Assert.Equal(NotificationChannel.None, decision.Channel);
        Assert.Equal("viewing target conversation", decision.Reason);
    }

    [Fact]
    public void Decide_ViewingTargetApproval_SuppressesNotification()
    {
        var presence = new FakePresence { ViewedApprovalConversationId = "conversation-1" };

        var decision = _policy.Decide(
            Request(NotificationType.ApprovalRequired), presence, Settings());

        Assert.Equal(NotificationChannel.None, decision.Channel);
        Assert.Equal("viewing approval", decision.Reason);
    }

    /// <summary>
    /// <c>MarkUnread</c> is what separates "do not interrupt me" from "do not record this". Only
    /// the two viewing branches mean the latter; every other suppression must leave the sidebar
    /// badge intact, which is the whole reason the flag exists.
    /// </summary>
    [Theory]
    [InlineData("notifications disabled")]
    [InlineData("type disabled")]
    public void Decide_SuppressedBySettings_StillMarksUnread(string expectedReason)
    {
        var settings = Settings(enable: expectedReason == "type disabled");
        if (expectedReason == "type disabled") settings.NotifyAgentReplies = false;

        var decision = _policy.Decide(Request(), new FakePresence(), settings);

        Assert.Equal(NotificationChannel.None, decision.Channel);
        Assert.Equal(expectedReason, decision.Reason);
        Assert.True(decision.MarkUnread);
    }

    [Fact]
    public void Decide_ViewingTargetConversation_MarksNothingUnread()
    {
        var presence = new FakePresence { ViewedConversationId = "conversation-1" };

        var decision = _policy.Decide(Request(), presence, Settings());

        Assert.False(decision.MarkUnread);
    }

    /// <summary>Viewing is checked ahead of the settings switches, so a user with notifications
    /// off does not accumulate unread counts for the conversation they are reading — nothing on
    /// that page would clear a badge it never earned by being away from it.</summary>
    [Fact]
    public void Decide_ViewingTargetWithNotificationsDisabled_PrefersTheViewingVerdict()
    {
        var presence = new FakePresence { ViewedConversationId = "conversation-1" };

        var decision = _policy.Decide(Request(), presence, Settings(enable: false));

        Assert.Equal("viewing target conversation", decision.Reason);
        Assert.False(decision.MarkUnread);
    }

    [Fact]
    public void Decide_ForegroundOnAnotherView_ReturnsInAppNotification()
    {
        var presence = new FakePresence { IsForeground = true };

        var decision = _policy.Decide(Request(), presence, Settings());

        Assert.Equal(NotificationChannel.InApp, decision.Channel);
        Assert.Equal("foreground, other view", decision.Reason);
        Assert.True(decision.PlaySound);
        Assert.Equal("Response body", decision.Body);
    }

    [Fact]
    public void Decide_Background_ReturnsSystemNotification()
    {
        var decision = _policy.Decide(Request(), new FakePresence(), Settings());

        Assert.Equal(NotificationChannel.System, decision.Channel);
        Assert.Equal("background", decision.Reason);
    }

    [Fact]
    public void Decide_MessagePreviewDisabled_ReplacesContentWithGenericBody()
    {
        var settings = Settings();
        settings.ShowMessagePreview = false;

        var decision = _policy.Decide(Request(), new FakePresence(), settings);

        Assert.Equal("You have a new response.", decision.Body);
    }

    private static NotificationRequest Request(NotificationType type = NotificationType.AgentReply) =>
        new("Agent", "Response body", type, ConversationId: "conversation-1");

    private static NotificationSettings Settings(bool enable = true) => new()
    {
        EnableNotifications = enable,
        NotifyAgentReplies = true,
        NotifyTaskCompletion = true,
        NotifyApprovalRequests = true,
        NotifyConnectionProblems = true,
        PlayNotificationSound = true,
        ShowMessagePreview = true,
    };

    private sealed class FakePresence : IWindowPresence
    {
        public bool IsForeground { get; init; }
        public string? ViewedConversationId { get; init; }
        public string? ViewedApprovalConversationId { get; init; }

        public bool IsViewingConversation(string? conversationId) =>
            conversationId == ViewedConversationId;

        public bool IsViewingApproval(string? conversationId) =>
            conversationId == ViewedApprovalConversationId;
    }
}
