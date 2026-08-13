namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// The universe of keys a shortcut may be bound to, as a two-way map between a Win32 virtual-key
/// code and the short label the UI shows on a keycap. A key absent from this table cannot be
/// captured or persisted — which is the point: it keeps a rebind to something undispatchable
/// (a dead key, a media key, IME state) from being offered at all.
///
/// Labels here are the same spellings the shortcut catalog already used ("Esc", not "Escape"),
/// so the pre-existing dialog renders identically after the rewrite. Lives beside
/// <see cref="KeyChord"/> rather than under Services because it is a table the model depends on,
/// and Models must not reach up into Services.
/// </summary>
public static class KeyNames
{
    // Win32 VK codes. Named rather than inlined because the punctuation ones are otherwise
    // unreadable — 192 says nothing; VK_OEM_3 with a "`" label says both halves.
    private const int VkBack = 8;
    private const int VkTab = 9;
    private const int VkEnter = 13;
    private const int VkShift = 16;
    private const int VkControl = 17;
    private const int VkAlt = 18;
    private const int VkEsc = 27;
    private const int VkSpace = 32;
    private const int VkPageUp = 33;
    private const int VkPageDown = 34;
    private const int VkEnd = 35;
    private const int VkHome = 36;
    private const int VkLeft = 37;
    private const int VkUp = 38;
    private const int VkRight = 39;
    private const int VkDown = 40;
    private const int VkInsert = 45;
    private const int VkDelete = 46;
    private const int VkNumpadAdd = 107;
    private const int VkNumpadSubtract = 109;
    private const int VkOemPlus = 187;      // "=" on a US layout
    private const int VkOemComma = 188;
    private const int VkOemMinus = 189;
    private const int VkOemPeriod = 190;
    private const int VkOemSemicolon = 186;
    private const int VkOemSlash = 191;
    private const int VkOemBacktick = 192;
    private const int VkOemOpenBracket = 219;
    private const int VkOemBackslash = 220;
    private const int VkOemCloseBracket = 221;
    private const int VkOemQuote = 222;

    private static readonly Dictionary<int, string> Names = BuildNames();
    private static readonly Dictionary<string, int> Codes = Names
        .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>
        {
            [VkBack] = "Backspace",
            [VkTab] = "Tab",
            [VkEnter] = "Enter",
            [VkEsc] = "Esc",
            [VkSpace] = "Space",
            [VkPageUp] = "PageUp",
            [VkPageDown] = "PageDown",
            [VkEnd] = "End",
            [VkHome] = "Home",
            [VkLeft] = "Left",
            [VkUp] = "Up",
            [VkRight] = "Right",
            [VkDown] = "Down",
            [VkInsert] = "Insert",
            [VkDelete] = "Delete",
            [VkOemSemicolon] = ";",
            [VkOemPlus] = "=",
            [VkOemComma] = ",",
            [VkOemMinus] = "-",
            [VkOemPeriod] = ".",
            [VkOemSlash] = "/",
            [VkOemBacktick] = "`",
            [VkOemOpenBracket] = "[",
            [VkOemBackslash] = "\\",
            [VkOemCloseBracket] = "]",
            [VkOemQuote] = "'",
        };

        for (var c = 'A'; c <= 'Z'; c++) names[c] = c.ToString();
        for (var d = '0'; d <= '9'; d++) names[d] = d.ToString();
        for (var f = 1; f <= 12; f++) names[0x70 + f - 1] = "F" + f;

        return names;
    }

    /// <summary>The keycap label, or a "Key123" placeholder so an unmapped code is still visible
    /// rather than blank. Anything reaching that branch failed <see cref="IsKnown"/> first.</summary>
    public static string ToDisplayName(int keyCode)
        => Names.TryGetValue(keyCode, out var name) ? name : "Key" + keyCode;

    public static bool TryGetKeyCode(string name, out int keyCode)
        => Codes.TryGetValue(name, out keyCode);

    public static bool IsKnown(int keyCode) => Names.ContainsKey(keyCode);

    /// <summary>True for the modifier keys themselves. Pressing one is not a chord — the capture
    /// control waits for a real key rather than binding "Ctrl".</summary>
    public static bool IsModifierKey(int keyCode)
        => keyCode is VkShift or VkControl or VkAlt;

    /// <summary>
    /// Folds numpad +/- onto the OEM keys that carry the same label. The zoom handlers accepted
    /// both spellings before this table existed (<c>(int)e.Key == 187 || e.Key == VirtualKey.Add</c>),
    /// and a chord can only name one key, so the alias has to collapse here or numpad zoom would
    /// quietly stop working.
    /// </summary>
    public static int Normalize(int keyCode) => keyCode switch
    {
        VkNumpadAdd => VkOemPlus,
        VkNumpadSubtract => VkOemMinus,
        _ => keyCode,
    };
}
