using ConnectOnion.WinUIClient.Models.Notifications;

namespace ConnectOnion.WinUIClient.Services.Notifications;

/// <summary>
/// In-app toast channel. Shows the toast in exactly one window — the foreground
/// one — so a single publish never produces duplicate cards across windows, and
/// marshals onto that window's UI thread.
/// </summary>
public sealed class InAppNotificationService : INotificationChannel
{
    private readonly WindowPresenceService _windows;

    public InAppNotificationService(WindowPresenceService windows) => _windows = windows;

    public void Show(NotificationRequest request, bool playSound)
    {
        var window = _windows.ForegroundWindow();
        if (window is null) return; // nothing visible to host it (policy shouldn't pick InApp then)

        var toast = new InAppToastModel(
            request.Title, request.Body, request.Type,
            request.AgentId, request.ConversationId, request.ActionId);

        window.DispatcherQueue.TryEnqueue(() =>
        {
            try { window.ShowInAppToast(toast); }
            catch (System.Exception ex) { NotificationLog.Warn("in-app toast failed", ex); }
        });
    }
}
