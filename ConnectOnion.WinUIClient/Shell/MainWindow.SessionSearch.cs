using ConnectOnion.WinUIClient.Controls;
using Microsoft.UI.Xaml;

namespace ConnectOnion.WinUIClient;

/// <summary>Main-window coordination for the session search overlay. The sidebar only raises
/// intent; this shell-owned partial controls modal lifetime and reuses the notification/tray
/// conversation-opening path so every entry point updates agent, session, navigation, and
/// sidebar state identically.</summary>
public sealed partial class MainWindow
{
    private async void ShowSessionSearchOverlay(FrameworkElement opener)
    {
        if (IsModalOverlayOpen && _sessionSearchOverlay?.IsOpen != true) return;
        await EnsureSessionSearchOverlay().ShowAsync(opener);
    }

    private void CloseSessionSearchOverlay()
        => _sessionSearchOverlay?.Hide();

    private async void SessionSearchOverlay_SessionSelected(
        object? sender,
        SessionSearchSelectionEventArgs e)
    {
        _sessionSearchOverlay?.Hide();
        await ShowConversationAsync(e.AgentId, e.SessionId);
    }
}
