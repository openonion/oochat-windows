using ConnectOnion.WinUIClient.Models.Notifications;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class NotificationModelsTests
{
    [Fact]
    public void EffectiveDedupKey_ExplicitKey_TakesPrecedence()
    {
        var request = new NotificationRequest("title", "body", NotificationType.AgentReply, DedupKey: "explicit");

        Assert.Equal("explicit", request.EffectiveDedupKey);
    }

    [Fact]
    public void EffectiveDedupKey_TaskAndActionIds_PrefersTaskIdentity()
    {
        var request = new NotificationRequest(
            "title", "body", NotificationType.TaskCompleted, TaskId: "task-1", ActionId: "action-1");

        Assert.Equal("task:task-1", request.EffectiveDedupKey);
    }

    [Fact]
    public void EffectiveDedupKey_NoExplicitIdentity_UsesStableRequestFields()
    {
        var first = new NotificationRequest("title", "body", NotificationType.Error, ConversationId: "conversation");
        var second = new NotificationRequest("title", "body", NotificationType.Error, ConversationId: "conversation");

        Assert.Equal(first.EffectiveDedupKey, second.EffectiveDedupKey);
        Assert.Contains("conversation", first.EffectiveDedupKey);
    }

    [Fact]
    public void Clone_Settings_ReturnsIndependentEquivalentSnapshot()
    {
        var settings = new NotificationSettings
        {
            EnableNotifications = false,
            NotifyAgentReplies = false,
            NotifyTaskCompletion = false,
            NotifyApprovalRequests = false,
            NotifyConnectionProblems = false,
            PlayNotificationSound = false,
            ShowMessagePreview = false,
        };

        var clone = settings.Clone();
        clone.EnableNotifications = true;

        Assert.False(settings.EnableNotifications);
        Assert.True(clone.EnableNotifications);
        Assert.False(clone.NotifyAgentReplies);
        Assert.False(clone.ShowMessagePreview);
    }
}
