using System;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>Visible when the string is non-empty, collapsed otherwise.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Colors the status dot by presence — green online, neutral offline, muted
/// otherwise. Color is a secondary signal here: every presence dot this feeds
/// is paired with a text label (see <c>PresenceLabel</c>/<c>StatusText</c> in
/// the view models), so presence is never conveyed by color alone.
/// </summary>
public sealed class PresenceToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is AgentPresence presence
            ? presence switch
            {
                AgentPresence.Online => "SuccessBrush",
                AgentPresence.Checking => "InfoBrush",
                AgentPresence.Offline => "TextTertiaryBrush",
                _ => "TextTertiaryBrush",
            }
            : "TextTertiaryBrush";
        return ThemeBrushResolver.Resolve(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Colors an event's status dot/badge: medium running, green done, darker error.
/// Running deliberately does NOT use BrandPrimaryBrush — Success is the brand green,
/// so the two would render identically and the dot would stop meaning anything.
/// See StatusRunningColor in Styles/Colors.xaml.
/// </summary>
public sealed class EventStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is EventStatus status
            ? status switch
            {
                EventStatus.Running => "StatusRunningBrush",
                EventStatus.Error => "DangerBrush",
                _ => "SuccessBrush",
            }
            : "TextTertiaryBrush";
        return ThemeBrushResolver.Resolve(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// The status color for an <em>interactive</em> card (ask_user / approval / plan_review).
/// Identical to <see cref="EventStatusToBrushConverter"/> except that Running maps to
/// Attention amber rather than StatusRunning gray: for these three, "running" means the
/// agent is blocked waiting on the user, which is the one in-flight state they can act on.
/// Pass <c>surface</c> as the converter parameter for the tinted wash instead of the solid
/// color — same three states, so a card's ground and its accent never disagree.
/// </summary>
public sealed class InteractiveStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var surface = string.Equals(parameter as string, "surface", StringComparison.OrdinalIgnoreCase);
        var key = value is EventStatus status
            ? status switch
            {
                EventStatus.Running => surface ? "AttentionSubtleBrush" : "AttentionBrush",
                EventStatus.Error => surface ? "DangerSubtleBrush" : "DangerBrush",
                _ => surface ? "SuccessSubtleBrush" : "SuccessBrush",
            }
            : surface ? "SurfaceElevatedBrush" : "TextTertiaryBrush";
        return ThemeBrushResolver.Resolve(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Theme-aware semantic tone for reusable interactive-card chrome.</summary>
public sealed class InteractiveToneToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var surface = string.Equals(parameter as string, "surface", StringComparison.OrdinalIgnoreCase);
        var key = value is InteractiveVisualTone tone
            ? tone switch
            {
                InteractiveVisualTone.Warning => surface ? "AttentionSubtleBrush" : "AttentionBrush",
                InteractiveVisualTone.Success => surface ? "SuccessSubtleBrush" : "SuccessBrush",
                InteractiveVisualTone.Danger => surface ? "DangerSubtleBrush" : "DangerBrush",
                _ => surface ? "SurfaceSecondaryBrush" : "BorderDefaultBrush",
            }
            : surface ? "SurfaceSecondaryBrush" : "BorderDefaultBrush";
        return ThemeBrushResolver.Resolve(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Picks one of two theme brushes from a bool, named in the converter parameter as
/// <c>"TrueKey|FalseKey"</c>. Exists so a selected ask_user option can carry an accent
/// border and fill without each of its four elements needing a converter of its own.
/// Brushes come from ThemeBrushResolver, which caches by key and drops the cache on
/// ThemeService.ThemeApplied — so a live theme flip still repaints.
/// </summary>
public sealed class BoolToResourceBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var keys = (parameter as string)?.Split('|');
        if (keys is not { Length: 2 })
        {
            return ThemeBrushResolver.Resolve("TextTertiaryBrush");
        }

        return ThemeBrushResolver.Resolve(value is true ? keys[0] : keys[1]);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Colors the compact connection legend under the composer. Every transport phase has a
/// dedicated composer-scoped brush so transitions remain distinguishable at a glance without
/// changing the status language used by tool cards and the rest of the application.
/// </summary>
public sealed class ConnectionPhaseToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is ConnectionPhase phase
            ? phase switch
            {
                ConnectionPhase.Connecting => "ComposerStatusConnectingBrush",
                ConnectionPhase.Connected => "ComposerStatusConnectedBrush",
                ConnectionPhase.Running => "ComposerStatusRunningBrush",
                ConnectionPhase.Waiting => "ComposerStatusWaitingBrush",
                ConnectionPhase.Reconnecting => "ComposerStatusReconnectingBrush",
                ConnectionPhase.Offline => "ComposerStatusOfflineBrush",
                _ => "ComposerStatusIdleBrush",
            }
            : "ComposerStatusIdleBrush";
        return ThemeBrushResolver.Resolve(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
