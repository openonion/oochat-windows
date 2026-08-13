namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// One labelled action and the key combination(s) that trigger it. A row may carry several
/// equivalent bindings (e.g. Ctrl+W and Esc), shown side by side.
///
/// <para>Customizable rows (<see cref="IsCustomizable"/>) carry an <see cref="Id"/> and a
/// <see cref="DefaultChord"/>, and are dispatched by looking their live chord up in
/// <c>KeyboardShortcutService</c> — rebinding one actually changes what fires. Rows that are not
/// customizable are listed for completeness and say why in <see cref="ReadOnlyReason"/>; the app
/// does not own their key, so offering to rebind them would be a lie.</para>
/// </summary>
public sealed class KeyboardShortcutItem
{
    /// <summary>Stable identifier ("file.newChat") used as the override key and the dispatch
    /// lookup. Empty for a non-customizable row, which has nothing to override.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public IReadOnlyList<KeyBinding> KeyBindings { get; set; } = new List<KeyBinding>();

    /// <summary>Extra terms folded into search beyond the name and key text (optional).</summary>
    public IReadOnlyList<string> SearchKeywords { get; set; } = new List<string>();

    /// <summary>The factory binding, and what "Reset" restores. <see cref="KeyChord.None"/> for a
    /// non-customizable row.</summary>
    public KeyChord DefaultChord { get; set; } = KeyChord.None;

    /// <summary>True when the app dispatches this shortcut itself and can therefore honour a
    /// rebind. See <see cref="ReadOnlyReason"/> for why the rest cannot.</summary>
    public bool IsCustomizable { get; set; }

    /// <summary>Why this row cannot be rebound, shown in place of its editor. Empty when
    /// <see cref="IsCustomizable"/>.</summary>
    public string ReadOnlyReason { get; set; } = "";

    /// <summary>Full spoken form of every binding ("Ctrl + N, or Esc"), set as the keycap
    /// group's AutomationProperties.Name so a screen reader reads the whole combo — the keycaps
    /// themselves are hidden from the accessibility tree.</summary>
    public string KeysAccessibleText => string.Join(", or ", KeyBindings.Select(b => b.DisplayText));

    /// <summary>Case-insensitive match across the action name, its key combinations (both the
    /// individual keys and the flat "Ctrl + N" form), and any extra keywords.</summary>
    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;

        if (Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;

        if (KeyBindings.Any(b =>
                b.DisplayText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || b.Keys.Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        return SearchKeywords.Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}
