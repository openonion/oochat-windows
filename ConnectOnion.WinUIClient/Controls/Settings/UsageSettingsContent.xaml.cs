using System;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// The Usage panel in Settings: per-model token totals for a chosen window, read from the usage
/// ledger. Loads on first show and on every range change; nothing is cached, because the ledger
/// changes underneath it whenever a turn finishes.
/// </summary>
public sealed partial class UsageSettingsContent : UserControl
{
    public UsageViewModel Vm { get; } = App.GetService<UsageViewModel>();

    /// <summary>Width at which the four totals fit on one row. Measured against this control,
    /// which is the settings modal's body — never the window, whose width says nothing about how
    /// much room the modal actually gives this panel.</summary>
    private const double WideTotalsMinWidth = 640;

    public UsageSettingsContent()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void UsageContent_SizeChanged(object sender, SizeChangedEventArgs e)
        => VisualStateManager.GoToState(
            this,
            e.NewSize.Width >= WideTotalsMinWidth ? "WideUsage" : "CompactUsage",
            useTransitions: false);

    /// <summary>Re-reads the ledger. Called by <see cref="SettingsOverlay"/> when the user navigates
    /// to this section, so the panel reflects turns that finished while Settings sat open.</summary>
    public async void Refresh()
    {
        ResetUsageLoadError();
        try
        {
            await Vm.LoadAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Usage history could not be loaded");
            UsageErrorBar.IsOpen = true;
        }
    }

    private async void Range_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag) return;
        if (!Enum.TryParse<UsageRange>(tag, out var range)) return;

        ResetUsageLoadError();
        try
        {
            await Vm.SetRangeAsync(range);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Usage range could not be loaded");
            UsageErrorBar.IsOpen = true;
        }
    }

    private void ResetUsageLoadError()
    {
        UsageErrorBar.Message = LocalizedStrings.Get(
            "UsageLoadErrorMessage",
            "ConnectOnion couldn't load usage history. Try opening this page again.");
        UsageErrorBar.IsOpen = false;
    }

    /// <summary>
    /// The only path in the app that deletes usage. Confirmed first: the ledger is deliberately not
    /// erased by deleting conversations, so this button is the single place a user can lose it, and
    /// it cannot be undone.
    /// </summary>
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizedStrings.Get("UsageClearTitle", "Clear usage history?"),
            Content = LocalizedStrings.Get(
                "UsageClearWarning",
                "This permanently deletes all recorded token usage on this device. Your conversations are not affected."),
            PrimaryButtonText = LocalizedStrings.Get("UsageClearConfirm", "Clear"),
            CloseButtonText = LocalizedStrings.Get("CommonCancel", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary) return;

        try
        {
            UsageErrorBar.IsOpen = false;
            await Vm.ClearAllAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Usage history could not be cleared");
            UsageErrorBar.Message = LocalizedStrings.Get(
                "UsageClearErrorMessage",
                "ConnectOnion couldn't clear usage history. Try again.");
            UsageErrorBar.IsOpen = true;
        }
    }
}
