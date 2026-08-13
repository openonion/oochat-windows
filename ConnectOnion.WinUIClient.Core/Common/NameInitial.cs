using System.Globalization;

namespace ConnectOnion.WinUIClient.Common;

/// <summary>
/// The single source of the letter shown on an agent's avatar tile.
///
/// It takes the first <i>text element</i> (grapheme), not the first UTF-16 code unit. Slicing a
/// string with <c>name[..1]</c> cuts an emoji or any astral-plane character straight through its
/// surrogate pair, and the avatar renders a replacement box instead of a character — the same bug
/// was independently written in four places, which is why this lives in one.
/// </summary>
public static class NameInitial
{
    public static string From(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";

        var enumerator = StringInfo.GetTextElementEnumerator(name.TrimStart());
        return enumerator.MoveNext()
            ? ((string)enumerator.Current).ToUpperInvariant()
            : "?";
    }

    /// <summary>
    /// Returns a compact two-grapheme monogram for avatar surfaces. Multi-word names use the
    /// first grapheme of the first two words (<c>Pan Kai</c> → <c>PK</c>); single-word names keep
    /// one grapheme so acronyms are never guessed from camel-case spelling.
    /// </summary>
    public static string FromPair(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";

        var parts = name.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return From(parts[0]);

        return From(parts[0]) + From(parts[1]);
    }
}
