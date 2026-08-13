using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.Views;
using Microsoft.UI.Xaml.Input;

namespace ConnectOnion.WinUIClient;

public sealed partial class MainWindow
{
    private void RegisterChatAccelerators()
        => RootGrid.KeyDown += ChatShortcut_KeyDown;

    /// <summary>
    /// Dispatches shortcuts that only make sense while a conversation is visible. This handler
    /// intentionally remains active while the composer text box has focus: the mode shortcut
    /// uses modifiers and does not conflict with typing or the composer's Enter behavior.
    /// </summary>
    private async void ChatShortcut_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || IsModalOverlayOpen) return;
        var shortcut = MatchShortcut(e);
        if (shortcut is null) return;
        if (ContentFrame.Content is not ChatPage page) return;

        switch (shortcut)
        {
            case KeyboardShortcutCatalog.Ids.CycleChatMode:
                e.Handled = true;
                await page.CycleApprovalModeAsync();
                break;

            // Both modifiers, so it stays clear of typing for the same reason the mode shortcut
            // does. It is a no-op when nothing is blocking, which is why it does not mark the
            // event handled in that case — a dead key press should not swallow the chord.
            case KeyboardShortcutCatalog.Ids.GoToPendingDecision:
                if (!page.GoToPendingDecision()) return;
                e.Handled = true;
                break;
        }
    }
}
