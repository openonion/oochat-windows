using System.Collections.Concurrent;
using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Applies the persisted theme to the app's root element. WinUI applies
/// <see cref="FrameworkElement.RequestedTheme"/> per element tree, so the
/// window registers its root here and the Settings page can re-apply live.
/// </summary>
public static class ThemeService
{
    private static FrameworkElement? _root;
    private static ThemeMode _theme = ThemeMode.System;
    private static ElementTheme _currentTheme = ElementTheme.Light;

    public static event System.Action<ElementTheme>? ThemeApplied;

    /// <summary>
    /// The concrete Light/Dark theme currently rendered by the window root. Popup-backed
    /// controls live in a separate element tree, so callers use this value when opening them
    /// instead of allowing those controls to fall back to the system theme.
    /// </summary>
    public static ElementTheme CurrentTheme => _currentTheme;

    public static void RegisterRoot(FrameworkElement root)
    {
        if (_root is not null)
        {
            _root.ActualThemeChanged -= Root_ActualThemeChanged;
        }

        _root = root;
        _root.ActualThemeChanged += Root_ActualThemeChanged;
        _currentTheme = ResolveActualTheme();
    }

    public static void UnregisterRoot(FrameworkElement root)
    {
        if (!ReferenceEquals(_root, root)) return;
        _root.ActualThemeChanged -= Root_ActualThemeChanged;
        _root = null;
    }

    public static void Apply(ThemeMode theme)
    {
        _theme = theme;
        if (_root is null) return;
        var requestedTheme = theme switch
        {
            ThemeMode.Dark => ElementTheme.Dark,
            ThemeMode.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        _root.RequestedTheme = requestedTheme;

        // ActualTheme is updated asynchronously after RequestedTheme changes. Publishing it here
        // used to broadcast the previous theme, leaving popup trees and the title bar one change
        // behind the window. Explicit Light/Dark choices are already concrete; System publishes
        // the root's current value now and ActualThemeChanged corrects it if Windows resolves to
        // the other theme on the next layout pass.
        PublishTheme(requestedTheme == ElementTheme.Default
            ? ResolveActualTheme()
            : requestedTheme);
    }

    private static ElementTheme ResolveActualTheme()
        => _root?.ActualTheme == ElementTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;

    private static void PublishTheme(ElementTheme theme)
    {
        _currentTheme = theme;
        ThemeApplied?.Invoke(theme);
    }

    /// <summary>
    /// Resolves a <see cref="Color"/> token straight from the app's Light/Dark
    /// <c>Styles/Colors.xaml</c> ThemeDictionaries, for the specific requested
    /// theme regardless of the app's current ambient theme. For the handful of
    /// call sites (Win2D canvas draws, <c>AppWindowTitleBar</c>) that need a raw
    /// <see cref="Color"/> rather than a brush and can't use
    /// <c>{ThemeResource}</c> markup — keeps them reading the same tokens pages
    /// bind to instead of duplicating hex literals.
    /// <c>Application.Resources.ThemeDictionaries</c> only reflects entries
    /// declared directly on that dictionary, not ones nested inside its merged
    /// dictionaries, so this walks <c>MergedDictionaries</c> to find the one
    /// (<c>Colors.xaml</c>) that actually declares <paramref name="key"/>.
    /// </summary>
    public static Color GetColor(string key, bool isDark)
        => ColorCache.GetOrAdd((key, isDark), static token => Lookup(token.Key, token.IsDark));

    /// <summary>
    /// The shared <see cref="SolidColorBrush"/> for a color token, for the call sites that build
    /// XAML elements in code and would otherwise mint a brush per element.
    ///
    /// <para>Sharing one instance across many elements is exactly what <c>{ThemeResource}</c> does
    /// — a resource-dictionary brush is referenced, not copied — so this is safe as long as
    /// nobody mutates the returned brush. Don't: set <see cref="SolidColorBrush.Color"/> on one of
    /// these and every element in the app using that token changes with it. A call site that needs
    /// a *mutable* brush (the composer's waveform, which recolors one instance in place) must keep
    /// building its own from <see cref="GetColor"/>.</para>
    ///
    /// <para>Keyed by theme as well as token, so a live theme flip resolves to a different brush
    /// rather than needing invalidation. Both dictionaries are permanent by design: the token
    /// tables are compiled into <c>Colors.xaml</c> and cannot change at runtime, so an entry is
    /// valid for the life of the process and the set of keys is bounded by the palette.</para>
    /// </summary>
    public static SolidColorBrush GetBrush(string key, bool isDark)
        => BrushCache.GetOrAdd((key, isDark), static token => new SolidColorBrush(GetColor(token.Key, token.IsDark)));

    private static readonly ConcurrentDictionary<(string Key, bool IsDark), Color> ColorCache = new();
    private static readonly ConcurrentDictionary<(string Key, bool IsDark), SolidColorBrush> BrushCache = new();

    private static Color Lookup(string key, bool isDark)
    {
        var themeKey = isDark ? "Dark" : "Light";
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            if (merged.ThemeDictionaries.TryGetValue(themeKey, out var themeDictionaryObj)
                && themeDictionaryObj is ResourceDictionary themeDictionary
                && themeDictionary.TryGetValue(key, out var value))
            {
                return (Color)value;
            }
        }

        throw new System.Collections.Generic.KeyNotFoundException($"Theme color '{key}' not found in '{themeKey}'.");
    }

    private static void Root_ActualThemeChanged(FrameworkElement sender, object args)
    {
        var actualTheme = ResolveActualTheme();
        if (_currentTheme != actualTheme || _theme == ThemeMode.System)
            PublishTheme(actualTheme);
    }
}
