using System;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Broadcasts live message-font-size changes to open chat views, mirroring
/// <see cref="ThemeService"/>. Settings changes a persisted preference, but an already-open
/// <c>ChatPage</c> reads the size once in <c>LoadAsync</c>; without a live signal the
/// adjustment would only show after switching conversations or reopening — which reads as the
/// setting doing nothing. This is also the single home for the preference→pixel mapping the
/// chat renders at, so the load path and the live path can't drift apart.
/// </summary>
public static class MessageFontService
{
    /// <summary>Raised on the UI thread when the user picks a new message text size.</summary>
    public static event Action<double>? FontSizeChanged;

    /// <summary>Pixel size the chat bubbles render at for each preference step.</summary>
    public static double ToPixels(MessageFontSize size) => size switch
    {
        MessageFontSize.Sm => 12,
        MessageFontSize.Lg => 18,
        _ => 14,
    };

    /// <summary>Notifies open chat views of a new size. Called after the preference is updated.</summary>
    public static void Apply(MessageFontSize size) => FontSizeChanged?.Invoke(ToPixels(size));
}
