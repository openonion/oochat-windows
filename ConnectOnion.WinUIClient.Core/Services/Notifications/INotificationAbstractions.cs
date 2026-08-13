using ConnectOnion.WinUIClient.Models.Notifications;

namespace ConnectOnion.WinUIClient.Services.Notifications;

/// <summary>
/// Read-only view of window state the <see cref="NotificationPolicy"/> needs.
/// Kept WinUI-free so the policy is unit-testable with a fake. Implemented by
/// <c>WindowPresenceService</c>.
/// </summary>
public interface IWindowPresence
{
    /// <summary>True when at least one app window is active (focused) and visible
    /// (not minimized or hidden to tray).</summary>
    bool IsForeground { get; }

    /// <summary>True when a foreground window is currently showing the given conversation.</summary>
    bool IsViewingConversation(string? conversationId);

    /// <summary>True when the user is looking at the approval UI for the conversation
    /// (the approval bubble renders inline, so this mirrors <see cref="IsViewingConversation"/>).</summary>
    bool IsViewingApproval(string? conversationId);
}

/// <summary>A concrete delivery surface (system toast or in-app toast).</summary>
public interface INotificationChannel
{
    void Show(NotificationRequest request, bool playSound);
}

/// <summary>Supplies the current settings snapshot to the policy.</summary>
public interface INotificationSettingsProvider
{
    NotificationSettings Current { get; }
}

/// <summary>Delayed callback scheduling, abstracted so the connection-lost grace
/// period is testable without real timers.</summary>
public interface INotificationScheduler
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}

/// <summary>Persists conversation-level unread and approval attention state.</summary>
public interface IConversationAttentionStore
{
    Task MarkUnreadAsync(
        string conversationId,
        bool requiresAttention,
        CancellationToken cancellationToken = default);
}
