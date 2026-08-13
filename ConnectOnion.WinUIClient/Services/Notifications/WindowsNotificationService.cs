using System;
using ConnectOnion.WinUIClient.Models.Notifications;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ConnectOnion.WinUIClient.Services.Notifications;

/// <summary>
/// System toast channel via Windows App SDK App Notifications. Builds a toast
/// carrying the routing arguments (<c>action</c>/<c>agentId</c>/<c>conversationId</c>/
/// <c>taskId</c>/<c>actionId</c>) that <c>NotificationActivationRouter</c> reads on
/// click. Degrades silently if the API is unavailable.
/// </summary>
public sealed class WindowsNotificationService : INotificationChannel
{
    public void Show(NotificationRequest request, bool playSound)
    {
        if (!AppNotificationCapability.IsAvailable) return;

        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(request.Title)
                .AddText(request.Body)
                .AddArgument("action", ActionFor(request.Type));

            if (!string.IsNullOrEmpty(request.AgentId)) builder.AddArgument("agentId", request.AgentId);
            if (!string.IsNullOrEmpty(request.ConversationId)) builder.AddArgument("conversationId", request.ConversationId);
            if (!string.IsNullOrEmpty(request.TaskId)) builder.AddArgument("taskId", request.TaskId);
            if (!string.IsNullOrEmpty(request.ActionId)) builder.AddArgument("actionId", request.ActionId);

            if (!playSound) builder.MuteAudio();

            NotificationLog.Info($"Show system toast: action={ActionFor(request.Type)} agentId={request.AgentId} conversationId={request.ConversationId}");
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            // API missing / notifications blocked by policy / OS error — never throw.
            NotificationLog.Warn("system notification failed", ex);
        }
    }

    private static string ActionFor(NotificationType type) => type switch
    {
        NotificationType.ApprovalRequired => "openApproval",
        _ => "openConversation",
    };
}
