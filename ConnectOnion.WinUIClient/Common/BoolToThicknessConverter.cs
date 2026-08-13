using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>
/// Picks one of two <see cref="Thickness"/> values from a bool, so a layout that changes shape
/// with its content can keep both spacings in the XAML beside the element they apply to rather
/// than hard-coded in C#.
///
/// <para><c>ConverterParameter</c> is <c>"&lt;when-true&gt;|&lt;when-false&gt;"</c>, each side
/// written the way a Thickness attribute normally is — <c>"9,5,0,0"</c>, <c>"4,2"</c> or
/// <c>"6"</c>. A missing or unparseable side is zero, because a wrong-but-present margin is far
/// easier to see and fix than a binding that threw.</para>
/// </summary>
public sealed class BoolToThicknessConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var sides = (parameter as string ?? "").Split('|');
        var index = value is bool flag && flag ? 0 : 1;
        return Parse(index < sides.Length ? sides[index] : null);
    }

    /// <summary>XAML's own Thickness shorthand: one value for all sides, two for
    /// horizontal/vertical, four for left/top/right/bottom.</summary>
    private static Thickness Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new Thickness(0);

        var parts = raw.Split(',');
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return new Thickness(0);
        }

        return values.Length switch
        {
            1 => new Thickness(values[0]),
            2 => new Thickness(values[0], values[1], values[0], values[1]),
            4 => new Thickness(values[0], values[1], values[2], values[3]),
            _ => new Thickness(0),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
