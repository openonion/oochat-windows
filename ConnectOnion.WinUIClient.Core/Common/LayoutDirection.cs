using System;
using System.Globalization;

namespace ConnectOnion.WinUIClient.Common;

/// <summary>
/// Whether a UI language lays out right-to-left.
///
/// <para>Kept WinUI-free and driven off <see cref="CultureInfo"/> rather than a hardcoded list of
/// the app's current languages, so adding a locale to <c>Strings/</c> is all it takes — there is
/// no second place to remember to update. <c>zh-CN</c> and <c>en-US</c> are both left-to-right
/// today, which means this returns false for everything the app currently ships; that is the
/// point of having it now rather than discovering the assumption later.</para>
/// </summary>
public static class LayoutDirection
{
    /// <summary>True when <paramref name="languageTag"/> names a right-to-left script.</summary>
    public static bool IsRightToLeft(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag)) return false;
        try
        {
            return CultureInfo.GetCultureInfo(languageTag).TextInfo.IsRightToLeft;
        }
        catch (CultureNotFoundException)
        {
            // An unrecognised tag is not a reason to mirror the whole shell.
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
