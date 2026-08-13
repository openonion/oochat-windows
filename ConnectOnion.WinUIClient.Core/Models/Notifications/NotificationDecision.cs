namespace ConnectOnion.WinUIClient.Models.Notifications;

/// <summary>Where a notification should be shown (if anywhere).</summary>
public enum NotificationChannel
{
    /// <summary>Suppressed — e.g. the user is already looking at the target.</summary>
    None,
    /// <summary>A lightweight in-app toast in the foreground window.</summary>
    InApp,
    /// <summary>A Windows App Notification (toast) via the OS.</summary>
    System,
}

/// <summary>The <c>NotificationPolicy</c>'s verdict for one request.</summary>
/// <param name="MarkUnread">
/// Whether the conversation should still be marked unread. Deliberately independent of
/// <see cref="Channel"/>: a toast and a sidebar badge answer different questions, and the two
/// were once the same decision — so turning notifications off in Settings also silently stopped
/// the sidebar from ever showing an unread count again. "Do not interrupt me" is not "do not
/// record that this happened". The one suppression that <i>does</i> mean the latter is the user
/// already looking at the conversation, which is what <see cref="AlreadySeen"/> is for.
/// </param>
public sealed record NotificationDecision(
    NotificationChannel Channel,
    string Reason,
    bool PlaySound = false,
    string? Body = null,
    bool MarkUnread = true)
{
    /// <summary>Show nothing, but still count the message as unread.</summary>
    public static NotificationDecision Suppress(string reason)
        => new(NotificationChannel.None, reason);

    /// <summary>Show nothing <i>and</i> record nothing — the user is already reading this
    /// conversation, so there is no unread message to badge.</summary>
    public static NotificationDecision AlreadySeen(string reason)
        => new(NotificationChannel.None, reason, MarkUnread: false);
}
