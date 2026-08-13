using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>
/// Single source of truth for a sidebar row's background so selection and hover
/// never paint two independent, visually-conflicting overlays at once: bind
/// <c>Background</c> to <c>IsSelected</c> through this converter and
/// <c>Visibility</c> to <c>IsSelected || IsHovered</c> (the item's
/// <c>ShowRowBackground</c> property) — selected rows get the subtle
/// <c>SidebarItemSelectedBrush</c> wash (hovering a selected row keeps that
/// same wash instead of layering a second, differently-colored hover on top;
/// the left accent bar next to this Border is what actually marks
/// "selected" — see <c>SidebarSelectedAccentBrush</c> in ShellSidebar.xaml),
/// unselected-but-hovered rows get the neutral <c>SidebarItemHoverBrush</c>.
/// </summary>
public sealed class RowStateToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isSelected = value is bool b && b;
        var key = isSelected ? "SidebarItemSelectedBrush" : "SidebarItemHoverBrush";
        return ThemeBrushResolver.Resolve(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
