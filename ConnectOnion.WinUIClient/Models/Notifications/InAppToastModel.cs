namespace ConnectOnion.WinUIClient.Models.Notifications;

/// <summary>Data for a single in-app toast card.</summary>
public sealed record InAppToastModel(
    string Title,
    string Body,
    NotificationType Type,
    string? AgentId,
    string? ConversationId,
    string? ActionId);
