using System;
using ConnectOnion.WinUIClient.Models.Notifications;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace ConnectOnion.WinUIClient;

/// <summary>
/// Help menu: keyboard shortcuts overview, the public docs, and the About overlay. Each item owns
/// exactly one command — the docs item only launches the browser, the About item only shows the
/// overlay. Both overlays are single, persistent instances hosted by MainWindow, so re-invoking a
/// menu item re-focuses the open one rather than creating a second.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The public docs. Internal rather than private because the first-run empty state on
    /// <see cref="Views.HomePage"/> links to the same place — a new user with no agent needs the
    /// docs more than anyone reaching them from the Help menu.</summary>
    internal const string DocsUrl = "https://docs.connectonion.com/";

    private void RegisterHelpMenuAccelerators()
        => RootGrid.KeyDown += HelpMenuShortcut_KeyDown;

    /// <summary>
    /// The layout-aware '/' matching this used to do by hand now lives in <see cref="LayoutKeys"/>,
    /// which <see cref="MatchShortcut"/> applies to every binding — so this shortcut keeps working
    /// on layouts where '/' is not VK_OEM_2, and gained the same rebinding the rest have.
    /// </summary>
    private void HelpMenuShortcut_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;

        // While a modal overlay is open it owns all input — don't fire background
        // accelerators behind it.
        if (IsModalOverlayOpen) return;

        if (MatchShortcut(e) == KeyboardShortcutCatalog.Ids.KeyboardShortcuts)
        {
            e.Handled = true;
            ShowKeyboardShortcutsOverlay(HelpMenuBarItem);
        }
    }

    private void KeyboardShortcuts_Click(object sender, RoutedEventArgs e)
        // Return focus to the top-level "Help" menu bar item, not the clicked flyout item —
        // the flyout is gone by the time the dialog closes, so focusing it would no-op.
        => ShowKeyboardShortcutsOverlay(HelpMenuBarItem);

    private void ShowKeyboardShortcutsOverlay(FrameworkElement? opener = null)
        => EnsureKeyboardShortcutsDialog().Show(opener);

    private void CloseKeyboardShortcutsOverlay()
        => _keyboardShortcutsDialog?.Hide();

    /// <summary>Opens the public docs in the default browser. Goes through
    /// <see cref="AppServices.UriLauncher"/> (rather than the static, unmockable <c>Launcher</c>)
    /// so this path is testable; surfaces a toast rather than failing silently when the shell can't
    /// hand the URI off (no browser registered, blocked by policy).</summary>
    private async void ConnectOnionDocs_Click(object sender, RoutedEventArgs e)
    {
        var launched = await AppServices.UriLauncher.LaunchAsync(new Uri(DocsUrl));

        if (!launched)
        {
            ShowInAppToast(new InAppToastModel(
                "Couldn't open the docs",
                $"Visit {DocsUrl} in your browser.",
                NotificationType.Error,
                AgentId: null,
                ConversationId: null,
                ActionId: null));
        }
    }

    private void AboutConnectOnion_Click(object sender, RoutedEventArgs e)
        => ShowAboutOverlay(HelpMenuBarItem);

    private void ShowAboutOverlay(FrameworkElement? opener = null)
        => EnsureAboutOverlay().Show(opener);

    private void CloseAboutOverlay()
        => _aboutOverlay?.Hide();
}
