using System.Collections.Generic;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>
/// Resolves a themed <see cref="Brush"/> from <c>Application.Current.Resources</c> by key.
///
/// <para><b>Cached, and invalidated on a theme flip.</b> The lookup itself is a dictionary walk
/// over the merged application resources, and the callers are value converters — they run once
/// per bound property per row, every time a virtualized <c>ListView</c> or <c>ItemsRepeater</c>
/// recycles a container. Scrolling a long transcript therefore performed tens of these per frame
/// to return the same handful of brushes. The reason it was uncached is real, though: the brush
/// behind a key changes when the user flips light/dark, and a stale instance keeps painting the
/// old theme. So the cache is cleared from <see cref="ThemeService.ThemeApplied"/>, which is the
/// event that makes the old answers wrong — the correctness argument is preserved, the per-call
/// cost is not.</para>
///
/// <para>A missing key returns <c>null</c> rather than throwing, and returning <c>null</c> to a
/// binding fails deep inside the binding engine with no useful stack. So this ends in a neutral
/// grey shown only if the palette itself failed to merge — a visibly-wrong colour is the better
/// failure. That last-resort grey lived copied across every resource-reading converter and
/// code-behind; it now lives here once.</para>
/// </summary>
internal static class ThemeBrushResolver
{
    // Mid-grey, legible on both light and dark. Shared instance: it is only ever assigned when a
    // key is missing (never in practice), and a Brush may back many elements safely.
    private static readonly Brush PaletteMissingFallback =
        new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));

    // Only ever touched from the UI thread: converters run during layout, and ThemeApplied is
    // raised from ThemeService.Apply, which is a UI-thread call. A lock would be pure overhead
    // on the exact path this cache exists to make cheap.
    private static readonly Dictionary<string, Brush> Cache = new(System.StringComparer.Ordinal);

    static ThemeBrushResolver() => ThemeService.ThemeApplied += _ => Cache.Clear();

    /// <summary>
    /// Returns the brush at <paramref name="key"/>, else the brush at <paramref name="fallbackKey"/>
    /// (when given), else the palette-missing grey.
    /// </summary>
    public static Brush Resolve(string key, string? fallbackKey = null)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var brush = Application.Current.Resources[key] as Brush
            ?? (fallbackKey is not null ? Application.Current.Resources[fallbackKey] as Brush : null)
            ?? PaletteMissingFallback;

        // The palette-missing grey is deliberately not cached: it means the resource dictionary
        // has not merged yet, which is a transient startup state, and caching it would pin every
        // affected key to grey for the life of the process.
        if (!ReferenceEquals(brush, PaletteMissingFallback)) Cache[key] = brush;
        return brush;
    }
}
