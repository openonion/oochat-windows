using System.Globalization;

namespace ConnectOnion.WinUIClient.Common;

/// <summary>
/// Formats the user-visible position beside a recovery word using the active locale.
/// The mnemonic words remain protocol data and are never transformed.
/// </summary>
public static class RecoveryPhraseNumberFormatter
{
    public static string Format(int position, CultureInfo? culture = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        return position.ToString(culture ?? CultureInfo.CurrentCulture);
    }
}
