namespace ConnectOnion.WinUIClient.Models.Notifications;

/// <summary>The kind of event a notification represents. Drives per-type
/// settings gating, generic-body text, and the activation action.</summary>
public enum NotificationType
{
    AgentReply,
    ApprovalRequired,
    TaskCompleted,
    ConnectionLost,
    Error,
}
