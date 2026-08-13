using System;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ConnectOnion.WinUIClient.Controls;

public sealed partial class PlanReviewCard : UserControl
{
    public PlanReviewCard() => InitializeComponent();
    public ChatMessage Message { get => (ChatMessage)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(ChatMessage), typeof(PlanReviewCard), new PropertyMetadata(null));
    public event RoutedEventHandler? ApproveRequested;
    public event RoutedEventHandler? ChangesRequested;
    public event RoutedEventHandler? RejectRequested;
    private void Approve_Click(object sender, RoutedEventArgs e) => ApproveRequested?.Invoke(this, e);
    private async void ReviewSections_Click(object sender, RoutedEventArgs e)
    {
        var isReadOnly = !Message.IsInteractiveEditable;
        var dialog = new PlanSectionReviewDialog(Message.EventDetail ?? "", isReadOnly) { XamlRoot = XamlRoot };
        var result = await dialog.ShowThemedAsync();
        if (isReadOnly) return;
        if (result == ContentDialogResult.None) return;

        Message.PlanFeedback = dialog.BuildFeedback();
        if (result == ContentDialogResult.Primary)
            ApproveRequested?.Invoke(this, new RoutedEventArgs());
        else
        {
            if (string.IsNullOrWhiteSpace(Message.PlanFeedback))
                Message.PlanFeedback = "Please revise the plan.";
            ChangesRequested?.Invoke(this, new RoutedEventArgs());
        }
    }
    private async void CopyPlan_Click(object sender, RoutedEventArgs e)
    {
        Services.ClipboardService.CopyText(Message.EventDetail ?? "");
        if (sender is not Button button) return;
        var original = button.Content;
        button.Content = Common.LocalizedStrings.Get("CommonCopied", "Copied");
        button.IsEnabled = false;
        await System.Threading.Tasks.Task.Delay(800);
        button.Content = original;
        button.IsEnabled = true;
    }
    private void RequestChanges_Click(object sender, RoutedEventArgs e) => Message.OpenPlanFeedback();
    private void SendFeedback_Click(object sender, RoutedEventArgs e) => ChangesRequested?.Invoke(this, e);
    private void CancelFeedback_Click(object sender, RoutedEventArgs e) => Message.ClosePlanFeedback();
    private void Reject_Click(object sender, RoutedEventArgs e) => Message.OpenPlanRejectConfirmation();
    private void CancelReject_Click(object sender, RoutedEventArgs e) => Message.ClosePlanRejectConfirmation();
    private void ConfirmReject_Click(object sender, RoutedEventArgs e) => RejectRequested?.Invoke(this, e);
    private void Feedback_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.Enter && Message.CanSendPlanFeedback)
        {
            e.Handled = true;
            ChangesRequested?.Invoke(this, new RoutedEventArgs());
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Message.ClosePlanFeedback();
        }
    }
}
