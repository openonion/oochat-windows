using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>
/// One rendered line of a unified diff. Plain <c>{ get; set; }</c> properties on purpose —
/// a type reached from an <c>x:Bind</c> DataTemplate breaks the generated XamlTypeInfo
/// metadata if it uses <c>required</c> or <c>init</c> accessors.
/// </summary>
public sealed class DiffLine
{
    public string Text { get; set; } = "";

    /// <summary>"add", "remove", "meta" (hunk headers and file markers) or "context".</summary>
    public string Kind { get; set; } = "context";
}

/// <summary>
/// Splits a unified diff into classified lines for the diff_preview card.
/// <para>Classification is by leading character, which is all a unified diff gives you. The
/// order of the checks matters: <c>---</c>/<c>+++</c> file markers start with the same
/// characters as removals and additions, so they have to be recognized first or every diff
/// renders with two miscolored lines at the top.</para>
/// </summary>
public sealed class DiffTextToLinesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var lines = new List<DiffLine>();
        if (value is not string text || text.Length == 0) return lines;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            lines.Add(new DiffLine { Text = raw, Kind = Classify(raw) });
        }
        return lines;
    }

    private static string Classify(string line)
    {
        if (line.StartsWith("+++", StringComparison.Ordinal)
            || line.StartsWith("---", StringComparison.Ordinal)
            || line.StartsWith("@@", StringComparison.Ordinal))
        {
            return "meta";
        }
        if (line.StartsWith('+')) return "add";
        if (line.StartsWith('-')) return "remove";
        return "context";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="DiffLine.Kind"/> to a brush. Pass <c>foreground</c> as the converter
/// parameter for the text color; the default is the row's background wash.
/// Resources are read fresh every call so a live theme flip repaints.
/// </summary>
public sealed class DiffKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var kind = value is ConnectOnion.WinUIClient.Models.DiffLineKind typed
            ? typed.ToString().ToLowerInvariant()
            : value as string ?? "context";
        var foreground = string.Equals(parameter as string, "foreground", StringComparison.OrdinalIgnoreCase);

        var key = foreground
            ? kind switch
            {
                // Body text stays at full contrast on the tinted rows — the wash already says
                // which way the line goes, and recoloring the code as well makes it harder to
                // read for no extra information.
                "add" or "addition" or "remove" or "deletion" => "TextPrimaryBrush",
                "meta" => "TextTertiaryBrush",
                _ => "TextSecondaryBrush",
            }
            : kind switch
            {
                "add" or "addition" => "SuccessSubtleBrush",
                "remove" or "deletion" => "DangerSubtleBrush",
                _ => "TransparentBrushFallback",
            };

        if (Application.Current.Resources.TryGetValue(key, out var brush)) return (Brush)brush;
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
