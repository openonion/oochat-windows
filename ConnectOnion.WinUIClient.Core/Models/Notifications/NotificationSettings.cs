namespace ConnectOnion.WinUIClient.Models.Notifications;

/// <summary>
/// User-configurable notification preferences. Persisted as JSON in the
/// <c>app_meta</c> table (see <c>NotificationSettingsStore</c>) so no
/// <c>preferences</c>-table schema change is needed. Plain get/set properties
/// keep it source-generator- and trim-friendly.
/// </summary>
public sealed class NotificationSettings
{
    public bool EnableNotifications { get; set; } = true;
    public bool NotifyAgentReplies { get; set; } = true;
    public bool NotifyTaskCompletion { get; set; } = true;
    public bool NotifyApprovalRequests { get; set; } = true;
    public bool NotifyConnectionProblems { get; set; } = true;
    public bool PlayNotificationSound { get; set; } = true;
    public bool ShowMessagePreview { get; set; } = true;

    public NotificationSettings Clone() => new()
    {
        EnableNotifications = EnableNotifications,
        NotifyAgentReplies = NotifyAgentReplies,
        NotifyTaskCompletion = NotifyTaskCompletion,
        NotifyApprovalRequests = NotifyApprovalRequests,
        NotifyConnectionProblems = NotifyConnectionProblems,
        PlayNotificationSound = PlayNotificationSound,
        ShowMessagePreview = ShowMessagePreview,
    };
}
