using System.Collections.Generic;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient;

/// <summary>
/// Keeps the menu bar's shortcut hints honest. The accelerator text beside each item used to be a
/// XAML literal, which was fine while the bindings were themselves hard-coded — now that they can
/// be rebound, a literal would advertise a combination that no longer does anything.
///
/// <para>Only the customizable items are listed. The Edit group keeps its literals because
/// <c>TextBox</c> owns those keys and they cannot be rebound (see
/// <see cref="KeyboardShortcutCatalog"/>), so their text can never drift.</para>
/// </summary>
public sealed partial class MainWindow
{
    private KeyboardShortcutService? _shortcuts;

    private void RegisterShortcutHints()
    {
        _shortcuts = AppServices.Shortcuts;
        _shortcuts.BindingsChanged += OnBindingsChanged;
        SyncMenuShortcutText();
    }

    private void DetachShortcutHints()
    {
        if (_shortcuts is null) return;
        _shortcuts.BindingsChanged -= OnBindingsChanged;
        _shortcuts = null;
    }

    /// <summary>Fires off the UI thread when a rebind lands (the service saves on a background
    /// task), so hop back before touching the menu.</summary>
    private void OnBindingsChanged()
        => DispatcherQueue.TryEnqueue(SyncMenuShortcutText);

    private void SyncMenuShortcutText()
    {
        if (_shortcuts is null) return;

        foreach (var (item, id) in MenuItemsByShortcutId())
        {
            var chord = _shortcuts.GetChord(id);
            // "Ctrl+Shift+N" — the compact spelling the menu bar uses, not the spaced-out
            // "Ctrl + Shift + N" the keycaps render.
            if (!chord.IsEmpty) item.KeyboardAcceleratorTextOverride = chord.Canonical;
        }
    }

    private IEnumerable<(MenuFlyoutItem Item, string Id)> MenuItemsByShortcutId()
    {
        yield return (NewChatMenuItem, KeyboardShortcutCatalog.Ids.NewChat);
        yield return (OpenFolderMenuItem, KeyboardShortcutCatalog.Ids.OpenFolder);
        yield return (CloseWindowMenuItem, KeyboardShortcutCatalog.Ids.CloseWindow);
        yield return (SettingsMenuItem, KeyboardShortcutCatalog.Ids.OpenSettings);
        yield return (ExitMenuItem, KeyboardShortcutCatalog.Ids.Exit);
        yield return (ToggleSidebarMenuItem, KeyboardShortcutCatalog.Ids.ToggleSidebar);
        yield return (OpenTerminalMenuItem, KeyboardShortcutCatalog.Ids.OpenTerminal);
        yield return (FindMenuItem, KeyboardShortcutCatalog.Ids.Find);
        yield return (BackMenuItem, KeyboardShortcutCatalog.Ids.GoBack);
        yield return (ForwardMenuItem, KeyboardShortcutCatalog.Ids.GoForward);
        yield return (ZoomInMenuItem, KeyboardShortcutCatalog.Ids.ZoomIn);
        yield return (ZoomOutMenuItem, KeyboardShortcutCatalog.Ids.ZoomOut);
        yield return (FullScreenMenuItem, KeyboardShortcutCatalog.Ids.ToggleFullScreen);
        yield return (KeyboardShortcutsMenuItem, KeyboardShortcutCatalog.Ids.KeyboardShortcuts);
    }
}
