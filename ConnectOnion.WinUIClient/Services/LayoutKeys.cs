using System;
using System.Runtime.InteropServices;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Translates a pressed virtual key into the key the user actually typed, under the keyboard
/// layout they actually have.
///
/// <para>This generalizes what <c>MainWindow.HelpMenu.cs</c> previously did for '/' alone: a
/// shortcut stored as "Ctrl+Shift+/" means the slash <i>character</i>, but '/' sits on different
/// virtual keys across layouts (and on some it is a shifted digit), so matching VK_OEM_2 would
/// quietly mean "this shortcut does not exist" for those users. Every punctuation binding has the
/// same exposure — backtick, brackets, comma — so the translation belongs here rather than being
/// special-cased for one shortcut.</para>
///
/// <para>Lives in the WinUI layer because it P/Invokes the Win32 layout API;
/// <see cref="KeyNames"/> and the resolver stay platform-free and headlessly testable.</para>
/// </summary>
public static class LayoutKeys
{
    /// <summary>MapVirtualKey's MAPVK_VK_TO_CHAR: translate a virtual key to the character it
    /// produces under the *current* keyboard layout.</summary>
    private const uint MapVkToChar = 2;

    /// <summary>
    /// The canonical key code for what <paramref name="virtualKey"/> types here, or the key itself
    /// when the layout has nothing to say (function keys, Enter, Esc — none of which move) or when
    /// it types something outside the bindable set.
    /// </summary>
    public static int Normalize(int virtualKey)
    {
        // Numpad divide always types '/', whatever the layout.
        if (virtualKey == (int)Windows.System.VirtualKey.Divide
            && KeyNames.TryGetKeyCode("/", out var slash))
        {
            return slash;
        }

        try
        {
            // The low word holds the character; the high bit flags a dead key, which no bindable
            // key is.
            var mapped = MapVirtualKeyW((uint)virtualKey, MapVkToChar) & 0x7FFF;
            if (mapped != 0 && KeyNames.TryGetKeyCode(((char)mapped).ToString(), out var typed))
                return typed;
        }
        catch (EntryPointNotFoundException)
        {
            // Fall through to the key as pressed.
        }

        return virtualKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
}
