namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// One key combination, e.g. Ctrl+Shift+P. It is rendered as a row of individual keycaps
/// (see <see cref="Tokens"/>); <see cref="DisplayText"/> is the flat form used for search
/// matching and for the screen-reader announcement, so the combo is never expressed by the
/// visual keycaps alone.
/// </summary>
public sealed class KeyBinding
{
    public KeyBinding(params string[] keys)
    {
        Keys = keys;

        var tokens = new List<KeyToken>(keys.Length * 2);
        for (var i = 0; i < keys.Length; i++)
        {
            if (i > 0) tokens.Add(new KeyToken("+", isSeparator: true));
            tokens.Add(new KeyToken(keys[i], isSeparator: false));
        }
        Tokens = tokens;
    }

    /// <summary>The keys in order, e.g. ["Ctrl", "Shift", "P"].</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>Keycaps interleaved with "+" separators, bound by the item template.</summary>
    public IReadOnlyList<KeyToken> Tokens { get; }

    /// <summary>Flat text ("Ctrl + Shift + P") for search and accessibility.</summary>
    public string DisplayText => string.Join(" + ", Keys);
}

/// <summary>A single element in a rendered key combination: either a keycap or a "+" separator.
/// Kept as a tiny bindable type so the keycap/separator split lives in data, not in XAML.</summary>
public sealed class KeyToken
{
    public KeyToken(string text, bool isSeparator)
    {
        Text = text;
        IsSeparator = isSeparator;
    }

    public string Text { get; set; }
    public bool IsSeparator { get; set; }
    public bool IsKeycap => !IsSeparator;
}
