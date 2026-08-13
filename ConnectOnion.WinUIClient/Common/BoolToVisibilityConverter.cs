using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>
/// Maps <c>true</c> to <see cref="Visibility.Visible"/> and <c>false</c> to
/// <see cref="Visibility.Collapsed"/>. Pass "invert" as the parameter to flip it.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        var visible = value is Visibility v && v == Visibility.Visible;
        if (parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }
        return visible;
    }
}
