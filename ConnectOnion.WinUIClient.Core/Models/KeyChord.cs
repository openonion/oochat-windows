namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// One captured key combination: the three modifiers the app dispatches on plus a single
/// non-modifier key, held as a raw Win32 virtual-key code rather than <c>VirtualKey</c> so this
/// (and the resolver built on it) stays free of WinRT and testable headlessly. The WinUI layer
/// passes <c>(int)e.Key</c> straight in — the existing handlers already spoke raw codes for
/// punctuation (188 for comma, 192 for backtick, ...), so nothing is lost in translation.
///
/// The canonical string form ("Ctrl+Shift+N") is what gets persisted into
/// <c>preferences.shortcut_overrides_json</c>: it survives a keycode table change, and it is
/// legible if anyone ever reads the row by hand.
/// </summary>
public readonly record struct KeyChord(bool Ctrl, bool Shift, bool Alt, int KeyCode)
{
    /// <summary>No binding. Distinct from any real chord because a chord always names a key.</summary>
    public static readonly KeyChord None = new(false, false, false, 0);

    public bool IsEmpty => KeyCode == 0;

    /// <summary>Modifiers first, in the order the UI and the catalog spell them.</summary>
    public IReadOnlyList<string> DisplayKeys
    {
        get
        {
            if (IsEmpty) return Array.Empty<string>();

            var keys = new List<string>(4);
            if (Ctrl) keys.Add("Ctrl");
            if (Shift) keys.Add("Shift");
            if (Alt) keys.Add("Alt");
            keys.Add(KeyNames.ToDisplayName(KeyCode));
            return keys;
        }
    }

    /// <summary>The persisted form, e.g. "Ctrl+Shift+N". Round-trips through <see cref="TryParse"/>.</summary>
    public string Canonical => string.Join("+", DisplayKeys);

    public override string ToString() => Canonical;

    /// <summary>Renders as the keycap sequence the shortcut list and dialog already bind to.</summary>
    public KeyBinding ToBinding() => new(DisplayKeys.ToArray());

    /// <summary>
    /// Parses the canonical form. Rejects anything that names no key or an unknown one, so a
    /// hand-edited or stale override degrades to "fall back to the default" rather than to a
    /// binding that can never fire.
    /// </summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        bool ctrl = false, shift = false, alt = false;
        var key = 0;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length == 0) return false;

            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) { ctrl = true; continue; }
            if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase)) { shift = true; continue; }
            if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase)) { alt = true; continue; }

            // Two non-modifier keys is not a chord this app can dispatch.
            if (key != 0) return false;
            if (!KeyNames.TryGetKeyCode(token, out key)) return false;
        }

        if (key == 0) return false;

        chord = new KeyChord(ctrl, shift, alt, key);
        return true;
    }

    /// <summary>Parses, or returns <see cref="None"/> — for callers that treat junk as "unset".</summary>
    public static KeyChord ParseOrNone(string? text) => TryParse(text, out var chord) ? chord : None;

    /// <summary>Builds a chord from a raw key event, normalizing the key (see <see cref="KeyNames.Normalize"/>).
    /// Returns <see cref="None"/> when the key is a modifier on its own — holding Ctrl is not a chord.</summary>
    public static KeyChord FromKeyEvent(int keyCode, bool ctrl, bool shift, bool alt)
    {
        if (KeyNames.IsModifierKey(keyCode)) return None;

        var normalized = KeyNames.Normalize(keyCode);
        return KeyNames.IsKnown(normalized)
            ? new KeyChord(ctrl, shift, alt, normalized)
            : None;
    }
}
