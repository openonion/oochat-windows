using System;
using System.Collections.Generic;
using ConnectOnion.WinUIClient.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ConnectOnion.WinUIClient;

/// <summary>Lazy construction and lifetime wiring for shell-owned full-window modals.</summary>
public sealed partial class MainWindow
{
    private AddAgentForm? _addAgentOverlay;
    private SettingsOverlay? _settingsOverlay;
    private KeyboardShortcutsDialog? _keyboardShortcutsDialog;
    private AboutOverlay? _aboutOverlay;
    private SessionSearchOverlay? _sessionSearchOverlay;

    /// <summary>Whether the background is currently out of the Tab order, so repeated
    /// Visibility callbacks (five overlays, each firing on both edges) do not re-apply.</summary>
    private bool _backgroundTabSuppressed;

    /// <summary>Each background control's own IsTabStop, captured on the way in. Restoring a
    /// blanket <c>true</c> would make things focusable that never were.</summary>
    private readonly Dictionary<Control, bool> _restoreTabStop = new();

    private AddAgentForm EnsureAddAgentOverlay()
    {
        if (_addAgentOverlay is not null) return _addAgentOverlay;
        var overlay = new AddAgentForm();
        Canvas.SetZIndex(overlay, 1004);
        overlay.AgentAdded += AddAgentOverlay_AgentAdded;
        overlay.TabFocusNavigation = KeyboardNavigationMode.Cycle;
        TrackOverlayModality(overlay);
        FloatingOverlayLayer.Children.Add(overlay);
        return _addAgentOverlay = overlay;
    }

    private SettingsOverlay EnsureSettingsOverlay()
    {
        if (_settingsOverlay is not null) return _settingsOverlay;
        var overlay = new SettingsOverlay();
        Canvas.SetZIndex(overlay, 1001);
        overlay.CloseRequested += SettingsOverlay_CloseRequested;
        overlay.TabFocusNavigation = KeyboardNavigationMode.Cycle;
        TrackOverlayModality(overlay);
        FloatingOverlayLayer.Children.Add(overlay);
        return _settingsOverlay = overlay;
    }

    private KeyboardShortcutsDialog EnsureKeyboardShortcutsDialog()
    {
        if (_keyboardShortcutsDialog is not null) return _keyboardShortcutsDialog;
        var overlay = new KeyboardShortcutsDialog();
        Canvas.SetZIndex(overlay, 1002);
        overlay.CloseRequested += KeyboardShortcutsDialog_CloseRequested;
        overlay.TabFocusNavigation = KeyboardNavigationMode.Cycle;
        TrackOverlayModality(overlay);
        FloatingOverlayLayer.Children.Add(overlay);
        return _keyboardShortcutsDialog = overlay;
    }

    private AboutOverlay EnsureAboutOverlay()
    {
        if (_aboutOverlay is not null) return _aboutOverlay;
        var overlay = new AboutOverlay();
        Canvas.SetZIndex(overlay, 1003);
        overlay.CloseRequested += AboutOverlay_CloseRequested;
        overlay.TabFocusNavigation = KeyboardNavigationMode.Cycle;
        TrackOverlayModality(overlay);
        FloatingOverlayLayer.Children.Add(overlay);
        return _aboutOverlay = overlay;
    }

    private SessionSearchOverlay EnsureSessionSearchOverlay()
    {
        if (_sessionSearchOverlay is not null) return _sessionSearchOverlay;
        var overlay = new SessionSearchOverlay();
        Canvas.SetZIndex(overlay, 1005);
        overlay.CloseRequested += SessionSearchOverlay_CloseRequested;
        overlay.SessionSelected += SessionSearchOverlay_SessionSelected;
        overlay.TabFocusNavigation = KeyboardNavigationMode.Cycle;
        TrackOverlayModality(overlay);
        FloatingOverlayLayer.Children.Add(overlay);
        return _sessionSearchOverlay = overlay;
    }

    /// <summary>Makes an overlay's open/closed state drive the shell's focus scope.
    ///
    /// <para>Every one of these overlays defines <c>IsOpen</c> as
    /// <c>Visibility == Visibility.Visible</c>, so watching the dependency property catches every
    /// transition — including the ones the overlay performs on itself (its own Esc handler, a
    /// completed Add Agent) that the window is never told about. That is why this is a property
    /// callback and not five more event subscriptions: the events do not exist for all of them.
    /// </para></summary>
    private void TrackOverlayModality(FrameworkElement overlay)
        => overlay.RegisterPropertyChangedCallback(
            UIElement.VisibilityProperty, (_, _) => UpdateModalFocusScope());

    /// <summary>Takes the window's background out of the Tab order while a modal overlay is up,
    /// and puts it back afterwards.
    ///
    /// <para>These overlays are <c>UserControl</c>s in a floating layer rather than
    /// <c>ContentDialog</c>s, so nothing gives them a focus trap: keyboard shortcuts were already
    /// blocked (<see cref="IsModalOverlayOpen"/>), but Tab still walked straight out of the
    /// "modal" into the chat page and the menu bar behind it.</para>
    ///
    /// <para>The mechanism has to be per-<c>Control</c>. <c>TabFocusNavigation</c> is a
    /// <c>UIElement</c> property but <c>IsTabStop</c> is only on <c>Control</c>, and
    /// <c>Once</c> alone still admits one tab stop — it is the pair (a single stop, carried by a
    /// container that cannot itself take focus) that skips a subtree. <c>MainContentArea</c> and
    /// <c>TitleBarArea</c> are plain <c>Grid</c>s, so the setting is applied to the focusable
    /// controls they hold instead of to them.</para></summary>
    private void UpdateModalFocusScope()
    {
        var modal = IsModalOverlayOpen;
        if (modal == _backgroundTabSuppressed) return;
        _backgroundTabSuppressed = modal;

        // The page behind a modal is still loaded and still reporting itself to the notification
        // layer, so tell that layer the user cannot actually see it. Closing the modal re-exposes
        // the conversation, which is also when a badge earned while it was up gets cleared.
        _windowPresence?.SetContentObscured(this, modal);
        if (!modal) ClearAttentionForVisibleConversation();

        foreach (var control in BackgroundFocusRoots())
        {
            if (modal)
            {
                _restoreTabStop[control] = control.IsTabStop;
                control.IsTabStop = false;
                control.TabFocusNavigation = KeyboardNavigationMode.Once;
            }
            else
            {
                control.TabFocusNavigation = KeyboardNavigationMode.Local;
                if (_restoreTabStop.TryGetValue(control, out var wasTabStop))
                    control.IsTabStop = wasTabStop;
            }
        }

        if (!modal) _restoreTabStop.Clear();
    }

    /// <summary>The focusable roots of everything *behind* a modal overlay.</summary>
    private Control[] BackgroundFocusRoots() =>
    [
        ContentFrame,
        ShellSidebar,
        SidebarResizeHandle,
        TitleBarMenu,
        SidebarToggleButton,
        BackButton,
        ForwardButton,
    ];

    private void SettingsOverlay_CloseRequested(object? sender, EventArgs e) => CloseSettingsOverlay();
    private void KeyboardShortcutsDialog_CloseRequested(object? sender, EventArgs e) => CloseKeyboardShortcutsOverlay();
    private void AboutOverlay_CloseRequested(object? sender, EventArgs e) => CloseAboutOverlay();
    private void SessionSearchOverlay_CloseRequested(object? sender, EventArgs e) => CloseSessionSearchOverlay();

    private void ShutdownOverlays()
    {
        if (_addAgentOverlay is { } addAgent)
        {
            addAgent.AgentAdded -= AddAgentOverlay_AgentAdded;
            addAgent.Shutdown();
        }
        if (_settingsOverlay is { } settings)
        {
            settings.CloseRequested -= SettingsOverlay_CloseRequested;
            settings.Shutdown();
        }
        if (_keyboardShortcutsDialog is { } shortcuts)
            shortcuts.CloseRequested -= KeyboardShortcutsDialog_CloseRequested;
        if (_aboutOverlay is { } about) about.CloseRequested -= AboutOverlay_CloseRequested;
        if (_sessionSearchOverlay is { } search)
        {
            search.CloseRequested -= SessionSearchOverlay_CloseRequested;
            search.SessionSelected -= SessionSearchOverlay_SessionSelected;
            search.Hide();
            search.Dispose();
        }
    }
}
