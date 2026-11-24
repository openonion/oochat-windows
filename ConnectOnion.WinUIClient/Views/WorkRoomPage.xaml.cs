using System;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.ViewModels;
using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.Core;

namespace ConnectOnion.WinUIClient.Views;

// the logic for the workroom page frontend
public sealed partial class WorkRoomPage : Page
{
    public WorkRoomViewModel Vm { get; } = App.GetService<WorkRoomViewModel>();

    // initialises the page and disables caching for unique workrooms
    public WorkRoomPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Disabled;
        Unloaded += OnUnloaded;
    }

    // gets the selected workroom id and asks the frontend to load it
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string id && !string.IsNullOrWhiteSpace(id)) _ = Vm.LoadAsync(id);
    }

    // calculates the transcript width if the scrollable area is resized
    private void RoomScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateRoomContentWidth();

    // calculates the transcript and composer width if the window is resized
    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRoomContentWidth();
        UpdateComposerWidth();
    }

    // centres the transcript and limits it to the current viewport or app's max width
    private void UpdateRoomContentWidth()
    {
        if (RoomScrollViewer is null || RoomContentStack is null) return;
        var availableWidth = Math.Max(0, RoomScrollViewer.ActualWidth - 48);
        RoomContentStack.Width = Math.Min(1000, availableWidth);
    }

    // sets the workroom chat composer to normal chat composer width
    private void UpdateComposerWidth()
    {
        if (ComposerColumn is null) return;
        var maxWidth = (double)Application.Current.Resources["ChatComposerWidth"];
        ComposerColumn.Width = Math.Min(maxWidth, Math.Max(0, ActualWidth - 48));
    }

    // cleans up if unloaded
    private void OnUnloaded(object sender, RoutedEventArgs e) => Vm.Cleanup();

    // returns to chat if navigated back
    private void Back_Click(object sender, RoutedEventArgs e) { if (Frame?.CanGoBack == true) Frame.GoBack(); }

    // upon a click, sends draft to view model and puts keyboard focus on the composer
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        await Vm.SendAsync();
        ComposerBox.Focus(FocusState.Programmatic);
    }

    // enter = send, shift enter = newline for the composer
    private async void ComposerBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

        if (e.Key != VirtualKey.Enter || shiftDown) return;
        e.Handled = true;
        await Vm.SendAsync();
        ComposerBox.Focus(FocusState.Programmatic);
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await Vm.StopAsync();

    // copies the provider messages and shows success
    private async void CopyProviderResponse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: FluentIcon icon } button) return;
        if (button.DataContext is not ProviderWorkRoomMessage message || string.IsNullOrWhiteSpace(message.Text)) return;

        ClipboardService.CopyText(message.Text);
        var originalIcon = icon.Icon;
        button.IsHitTestVisible = false;

        icon.Icon = Icon.Checkmark;
        AutomationProperties.SetName(button, "Copied");
        await Task.Delay(1000);
        icon.Icon = originalIcon;
        button.IsHitTestVisible = true;

        AutomationProperties.SetName(button, "Copy response");
    }

    // confirms the changed permission elevation and asks the view model to request it
    private async void PermissionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: ProviderWorkRoomPermissionOption option } || option.Id == Vm.SelectedPermissionId) return;

        var elevated = option.Risk is "high" or "danger" or "critical" || option.NativeProfileId == ":danger-full-access";
        var confirmed = false;
        if (elevated)
        {
            if (XamlRoot is null) return;
            var dialog = new ContentDialog
            {
                Title = "Confirm elevated provider access",
                Content = $"Allow {Vm.Room?.ProviderDisplayName ?? "the coding agent"} to use “{option.Label}” for subsequent work? This permission is controlled by the host.",
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
            if (confirmed && option.NativeProfileId == ":danger-full-access")
            {
                var fullAccessDialog = new ContentDialog
                {
                    Title = "Confirm Full Access",
                    Content = "Full Access can let the coding agent read and change files beyond the current workspace. Confirm again to enable it.",
                    PrimaryButtonText = "Enable Full Access",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };
                confirmed = await fullAccessDialog.ShowAsync() == ContentDialogResult.Primary;
            }
            if (!confirmed)
            {
                Vm.RefreshPermissionSelection();
                return;
            }
        }

        await Vm.ChangePermissionAsync(option.Id, confirmed);
        Vm.RefreshPermissionSelection();
    }

    // the 3 functions below manage whether an approve is approved or rejected
    private async void ApprovalApprove_Click(object sender, RoutedEventArgs e)
        => await ResolveApprovalAsync(sender, approve: true);

    private async void ApprovalReject_Click(object sender, RoutedEventArgs e)
        => await ResolveApprovalAsync(sender, approve: false);

    private async Task ResolveApprovalAsync(object sender, bool approve)
    {
        if (sender is Button { Tag: ProviderWorkRoomApproval approval })
            await Vm.ResolveApprovalAsync(approval, approve);
    }
}
