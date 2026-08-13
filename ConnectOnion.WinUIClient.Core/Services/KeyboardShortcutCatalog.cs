using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// The single source of truth for the app's shortcuts — both the list the Keyboard shortcuts
/// dialog shows and, for the customizable half, the bindings the key handlers actually dispatch
/// on via <see cref="KeyboardShortcutService"/>. Adding a customizable entry here is what makes
/// it real; there is no second place to also edit.
///
/// <para><b>Customizable</b> entries are the ones the app dispatches itself, in
/// <c>MainWindow.FileMenu.cs</c>, <c>MainWindow.ViewMenu.cs</c>,
/// <c>MainWindow.ChatShortcuts.cs</c> and <c>MainWindow.HelpMenu.cs</c>.
/// Those handlers ask the service for the live chord, so changing <see cref="Ids"/> or a default
/// here changes behaviour with no handler edit.</para>
///
/// <para><b>Non-customizable</b> entries are listed so the overview stays complete, but the app
/// does not own their key and could not honour a rebind — each says so in its reason:</para>
/// <list type="bullet">
///   <item>Edit group → <c>TextBox</c> handles Ctrl+Z/Y/X/C/V/A natively and
///     <c>MainWindow.EditMenu.cs</c> deliberately steps aside while one is focused. Rebinding
///     "Copy" would not stop Ctrl+C from copying.</item>
///   <item>Chat group → <c>Controls/ChatComposer.xaml.cs</c>, and already switchable through the
///     Enter-key preference rather than a rebind.</item>
///   <item>Find group and "Close dialog" → contextual keys owned by whichever surface has focus
///     (<c>MainWindow.ViewMenu.cs</c>, <c>Views/ChatPage.Find.cs</c>, the overlays' Esc handling).</item>
/// </list>
/// </summary>
public static class KeyboardShortcutCatalog
{
    /// <summary>Stable override keys. These strings land in <c>preferences.shortcut_overrides_json</c>,
    /// so renaming one silently drops that user's rebind back to the default — don't.</summary>
    public static class Ids
    {
        public const string NewChat = "file.newChat";
        public const string OpenFolder = "file.openFolder";
        public const string OpenSettings = "file.openSettings";
        public const string CloseWindow = "file.closeWindow";
        public const string Exit = "file.exit";

        public const string ToggleSidebar = "view.toggleSidebar";
        public const string OpenTerminal = "view.openTerminal";
        public const string Find = "view.find";
        public const string GoBack = "view.goBack";
        public const string GoForward = "view.goForward";
        public const string ZoomIn = "view.zoomIn";
        public const string ZoomOut = "view.zoomOut";
        public const string ToggleFullScreen = "view.toggleFullScreen";

        public const string CycleChatMode = "chat.cycleMode";
        public const string GoToPendingDecision = "chat.goToPendingDecision";

        public const string KeyboardShortcuts = "general.keyboardShortcuts";
    }

    private const string TextBoxReason = "Handled by the system text box";
    private const string ComposerReason = "Set by the Enter key preference";
    private const string ContextualReason = "Fixed while this surface has focus";
    private const string DismissReason = "Fixed so a dialog can always be dismissed";

    // Virtual-key codes for the non-letter defaults, named so the table below reads as keys
    // rather than as magic numbers.
    private const int Comma = 188;
    private const int Backtick = 192;
    private const int OpenBracket = 219;
    private const int CloseBracket = 221;
    private const int Equal = 187;
    private const int Minus = 189;
    private const int Slash = 191;
    private const int F11 = 122;

    /// <summary>The catalog with every entry at its factory binding. <see cref="KeyboardShortcutService"/>
    /// layers the user's overrides over this to produce the live view.</summary>
    public static IReadOnlyList<KeyboardShortcutGroup> GetGroups() => new List<KeyboardShortcutGroup>
    {
        new("File", new List<KeyboardShortcutItem>
        {
            Custom(Ids.NewChat, "New chat", Ctrl('N')),
            Custom(Ids.OpenFolder, "Open folder", Ctrl('O')),
            Custom(Ids.OpenSettings, "Open settings", Ctrl(Comma)),
            Custom(Ids.CloseWindow, "Close window", Ctrl('W')),
            Custom(Ids.Exit, "Exit application", Ctrl('Q')),
        }),
        new("Edit", new List<KeyboardShortcutItem>
        {
            Fixed("Undo", TextBoxReason, Combo("Ctrl", "Z")),
            Fixed("Redo", TextBoxReason, Combo("Ctrl", "Y")),
            Fixed("Cut", TextBoxReason, Combo("Ctrl", "X")),
            Fixed("Copy", TextBoxReason, Combo("Ctrl", "C")),
            Fixed("Paste", TextBoxReason, Combo("Ctrl", "V")),
            Fixed("Select all", TextBoxReason, Combo("Ctrl", "A")),
        }),
        new("View", new List<KeyboardShortcutItem>
        {
            Custom(Ids.ToggleSidebar, "Toggle sidebar", Ctrl('B')),
            Custom(Ids.OpenTerminal, "Open terminal", Ctrl(Backtick)),
            Custom(Ids.Find, "Find", Ctrl('F')),
            Custom(Ids.GoBack, "Go back", Ctrl(OpenBracket)),
            Custom(Ids.GoForward, "Go forward", Ctrl(CloseBracket)),
            Custom(Ids.ZoomIn, "Zoom in", CtrlShift(Equal)),
            Custom(Ids.ZoomOut, "Zoom out", Ctrl(Minus)),
            Custom(Ids.ToggleFullScreen, "Toggle full screen", Plain(F11)),
        }),
        new("Chat", new List<KeyboardShortcutItem>
        {
            Custom(Ids.CycleChatMode, "Cycle approval mode", CtrlShift('M')),
            Custom(Ids.GoToPendingDecision, "Go to pending decision", CtrlShift('D')),
            Fixed("Send message", ComposerReason, Combo("Enter")),
            Fixed("Insert new line", ComposerReason, Combo("Shift", "Enter")),
        }),
        new("Find", new List<KeyboardShortcutItem>
        {
            Fixed("Next match", ContextualReason, Combo("Enter")),
            Fixed("Previous match", ContextualReason, Combo("Shift", "Enter")),
            Fixed("Close find", ContextualReason, Combo("Esc")),
        }),
        new("General", new List<KeyboardShortcutItem>
        {
            Custom(Ids.KeyboardShortcuts, "Keyboard shortcuts", CtrlShift(Slash)),
            Fixed("Close dialog", DismissReason, Combo("Esc")),
        }),
    };

    /// <summary>Every customizable entry, flattened — the resolver's working set.</summary>
    public static IReadOnlyList<KeyboardShortcutItem> GetCustomizable()
    {
        var items = new List<KeyboardShortcutItem>();
        foreach (var group in GetGroups())
            foreach (var item in group.Shortcuts)
                if (item.IsCustomizable)
                    items.Add(item);
        return items;
    }

    private static KeyboardShortcutItem Custom(string id, string name, KeyChord chord) => new()
    {
        Id = id,
        Name = name,
        DefaultChord = chord,
        IsCustomizable = true,
        KeyBindings = new[] { chord.ToBinding() },
    };

    private static KeyboardShortcutItem Fixed(string name, string reason, params KeyBinding[] bindings) => new()
    {
        Name = name,
        ReadOnlyReason = reason,
        IsCustomizable = false,
        KeyBindings = bindings,
    };

    private static KeyChord Ctrl(int key) => new(Ctrl: true, Shift: false, Alt: false, key);
    private static KeyChord CtrlShift(int key) => new(Ctrl: true, Shift: true, Alt: false, key);
    private static KeyChord Plain(int key) => new(Ctrl: false, Shift: false, Alt: false, key);

    private static KeyBinding Combo(params string[] keys) => new(keys);
}
