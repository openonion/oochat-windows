using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Models.Notifications;
using Microsoft.UI.Dispatching;

namespace ConnectOnion.WinUIClient.Services.Notifications;

/// <summary>
/// The operations the notification layer needs from a chat-hosting window,
/// without exposing the whole <c>MainWindow</c>. Implemented by <c>MainWindow</c>
/// and registered with <see cref="WindowPresenceService"/>.
/// </summary>
public interface IChatWindow
{
    DispatcherQueue DispatcherQueue { get; }

    /// <summary>Restore from tray/minimize and bring to the foreground.</summary>
    void RestoreAndActivate();

    /// <summary>Select the agent + conversation and navigate this window's frame to
    /// it. Must fail safe (no throw) when the conversation no longer exists.</summary>
    Task ShowConversationAsync(string agentId, string conversationId);

    /// <summary>Render a lightweight in-app toast in this window.</summary>
    void ShowInAppToast(InAppToastModel toast);
}
