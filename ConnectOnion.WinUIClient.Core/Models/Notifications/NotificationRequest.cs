namespace ConnectOnion.WinUIClient.Models.Notifications;

/// <summary>
/// A transport-agnostic notification the <c>NotificationCoordinator</c> decides
/// on. Carries only the routing identifiers and already-sanitized text — never
/// raw markdown, tool JSON, or exception stacks (that shaping happens before a
/// request is built, see <c>NotificationText</c>).
/// </summary>
public sealed record NotificationRequest(
    string Title,
    string Body,
    NotificationType Type,
    string? AgentId = null,
    string? ConversationId = null,
    string? TaskId = null,
    string? ActionId = null,
    string? DedupKey = null)
{
    /// <summary>A stable identity used to drop duplicate publishes of the same
    /// underlying event when no explicit <see cref="DedupKey"/> was supplied.</summary>
    public string EffectiveDedupKey =>
        DedupKey
        ?? (TaskId is not null ? $"task:{TaskId}"
            : ActionId is not null ? $"action:{ActionId}"
            : $"{Type}:{ConversationId}:{Title}:{Body}");
}
