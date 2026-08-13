using System;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Common;

/// <summary>
/// Keeps popup-backed dialogs on the same concrete theme as the app's registered root.
/// Setting only <see cref="FrameworkElement.XamlRoot"/> is not enough when the app overrides
/// <see cref="FrameworkElement.RequestedTheme"/> on its own element tree: a ContentDialog is
/// presented in a separate popup tree and can otherwise fall back to the Windows theme.
/// </summary>
public static class ContentDialogExtensions
{
    public static async Task<ContentDialogResult> ShowThemedAsync(this ContentDialog dialog)
    {
        void ApplyTheme(ElementTheme theme) => dialog.RequestedTheme = theme;

        ApplyTheme(ThemeService.CurrentTheme);
        ThemeService.ThemeApplied += ApplyTheme;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            ThemeService.ThemeApplied -= ApplyTheme;
        }
    }
}
