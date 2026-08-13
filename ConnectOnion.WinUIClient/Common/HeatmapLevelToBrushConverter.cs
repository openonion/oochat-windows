using System;
using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Xaml.Data;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>
/// Maps a heatmap intensity level (0–4) onto its brush from the app palette.
///
/// <para>Resolved from <c>Application.Current.Resources</c> on every call rather than cached in a
/// field: the brush behind a key changes when the user flips light/dark, and a cached
/// <see cref="Brush"/> would keep painting the old theme's colour until the control was rebuilt.
/// This is the same rule the other resource-reading converters in this folder follow.</para>
/// </summary>
public sealed class HeatmapLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var level = value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            _ => 0,
        };

        // Clamped rather than trusted: the level comes from a projection that already guarantees
        // 0–4, but a binding error or a future extra bucket must not throw inside the binding
        // engine, where the stack says nothing about where it came from.
        level = Math.Clamp(level, 0, UsageHeatmap.MaxLevel);

        // Neutral-token fallback and, behind it, the shared palette-missing grey — a missing key
        // returns null rather than throwing, and null would fail inside the binding engine. A
        // visibly-wrong grey beats an unexplained blank grid. See ThemeBrushResolver.
        return ThemeBrushResolver.Resolve($"HeatmapLevel{level}Brush", "SurfaceTertiaryBrush");
    }

    // One-way by nature: a brush cannot tell you which level produced it.
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
