using System;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>
/// Resolves a resource-key string into the <see cref="Style"/> it names.
///
/// <para>Exists so a card can pick its button emphasis from state without the view holding two
/// otherwise-identical buttons behind mutually exclusive <c>x:Load</c>. The approval card is the
/// case: when the command is destructive, emphasis moves from Allow to Decline, and everything
/// else about both buttons stays the same.</para>
///
/// <para>Unlike <c>ThemeBrushResolver</c> this deliberately does <b>not</b> cache. A
/// <see cref="Style"/> is theme-independent — the brushes inside it are <c>ThemeResource</c>
/// references that re-resolve themselves — so there is no stale-colour hazard to guard against,
/// and it is consulted once per approval card rather than per row per frame.</para>
/// </summary>
public sealed partial class ResourceStyleConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
        => value is string key
            && Application.Current.Resources.TryGetValue(key, out var resource)
            && resource is Style style
                ? style
                : null;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a FluentIcons icon <i>name</i> to its <see cref="Icon"/> enum value, so a view model can
/// choose an icon without taking a dependency on the icon package (Core cannot reference it).
/// Falls back to <see cref="Icon.Info"/> rather than throwing — a missing glyph should degrade to a
/// neutral mark, never take down the card that was trying to warn someone.
/// </summary>
public sealed partial class IconNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string name && Enum.TryParse<Icon>(name, ignoreCase: true, out var icon)
            ? icon
            : Icon.Info;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
