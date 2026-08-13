using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Data;
using Windows.UI.Text;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>
/// Maps <c>true</c> to <see cref="FontWeights.SemiBold"/> and <c>false</c> to
/// <see cref="FontWeights.Normal"/>. Used to lift a sidebar row's title when it is the
/// currently-open conversation without duplicating a VisualState per template.
/// </summary>
public sealed class BoolToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
