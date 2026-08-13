using System;
using System.Collections.Generic;
using System.Threading;
using ConnectOnion.WinUIClient.Controls;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace ConnectOnion.WinUIClient.Views;

/// <summary>
/// <see cref="ChatPage"/>: the per-message action row — copy, edit-and-resend, retry, and the
/// hover/focus chrome that reveals it. Grouped here because the reveal logic (pointer and focus
/// tracked together, so keyboard users get the same affordance) is a self-contained concern.
/// </summary>
public sealed partial class ChatPage
{
    private CancellationTokenSource? _uiFeedbackCts;
    private readonly Dictionary<Button, Action> _copyFeedbackRestores = new();

    private void StartUiFeedbackLifetime()
    {
        if (_uiFeedbackCts is not null)
            StopUiFeedbackLifetime(restoreVisuals: true);
        _uiFeedbackCts = new CancellationTokenSource();
    }

    private void StopUiFeedbackLifetime(bool restoreVisuals)
    {
        _uiFeedbackCts?.Cancel();
        _uiFeedbackCts?.Dispose();
        _uiFeedbackCts = null;

        if (restoreVisuals)
        {
            foreach (var restore in _copyFeedbackRestores.Values)
                restore();
        }
        _copyFeedbackRestores.Clear();
    }

    private async System.Threading.Tasks.Task ShowCopyFeedbackAsync(Button button, Action restore)
    {
        if (_uiFeedbackCts is not { IsCancellationRequested: false } lifetime) return;

        if (_copyFeedbackRestores.Remove(button, out var previous)) previous();
        _copyFeedbackRestores[button] = restore;
        try
        {
            await System.Threading.Tasks.Task.Delay(1000, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }

        if (!ReferenceEquals(_uiFeedbackCts, lifetime) ||
            !_copyFeedbackRestores.TryGetValue(button, out var current) ||
            !ReferenceEquals(current, restore))
        {
            return;
        }

        _copyFeedbackRestores.Remove(button);
        restore();
    }

    private async void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChatMessage message ||
            string.IsNullOrEmpty(message.Content))
        {
            return;
        }

        ClipboardService.CopyText(message.Content);

        if (sender is Button { Content: FluentIcon fluentIcon } iconButton)
        {
            var originalIcon = fluentIcon.Icon;
            iconButton.IsHitTestVisible = false;
            fluentIcon.Icon = Icon.Checkmark;
            AutomationProperties.SetName(iconButton, "Copied");
            await ShowCopyFeedbackAsync(iconButton, () =>
            {
                fluentIcon.Icon = originalIcon;
                var current = iconButton.DataContext as ChatMessage ?? message;
                AutomationProperties.SetName(
                    iconButton,
                    current.IsUser ? "Copy message" : "Copy response");
                iconButton.IsHitTestVisible = true;
            });
            return;
        }

        if (sender is Button { Content: IconText iconText })
        {
            var original = iconText.Text;
            iconText.Text = Common.LocalizedStrings.Get("CommonCopied", "Copied");
            await ShowCopyFeedbackAsync((Button)sender, () => iconText.Text = original);
        }
    }

    private static ChatMessage? MessageFrom(object sender)
        => (sender as FrameworkElement)?.DataContext as ChatMessage;

    private void MessageActions_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (MessageFrom(sender) is { IsEditing: false } message)
        {
            message.AreUserActionsVisible = true;
        }
    }

    private void MessageActions_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (MessageFrom(sender) is { IsEditing: false } message)
        {
            message.AreUserActionsVisible = false;
        }
    }

    private void MessageActions_GotFocus(object sender, RoutedEventArgs e)
    {
        if (MessageFrom(sender) is { IsEditing: false } message)
        {
            message.AreUserActionsVisible = true;
        }
    }

    private void MessageActions_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement root || MessageFrom(sender) is not { } message) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            var focused = FocusManager.GetFocusedElement(root.XamlRoot) as DependencyObject;
            if (!message.IsEditing && (focused is null || !IsDescendantOf(focused, root)))
            {
                message.AreUserActionsVisible = false;
            }
        });
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (DependencyObject? current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private void EditUserMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChatMessage message && !Vm.IsProcessing)
        {
            message.BeginEditing();
        }
    }

    private void CancelEditUserMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChatMessage message)
        {
            message.CancelEditing();
            message.AreUserActionsVisible = true;
        }
    }

    private async void SaveEditUserMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ChatMessage message) return;

        var replacement = message.EditDraft.Trim();
        if (replacement.Length == 0) return;

        message.CancelEditing();
        if (await Vm.BranchFromMessageAsync(message, replacement))
        {
            QueueScrollToEnd(force: true);
            return;
        }

        message.BeginEditing();
        message.EditDraft = replacement;
    }

    private async void RetryUserMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChatMessage message &&
            await Vm.RetryUserMessageAsync(message))
        {
            QueueScrollToEnd(force: true);
        }
    }

    private async void RetryAgentMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChatMessage message &&
            await Vm.RetryFromAgentMessageAsync(message))
        {
            QueueScrollToEnd(force: true);
        }
    }

    /// <summary>Folds a grouped thought-process card. Only reachable on a card with more than one
    /// step — a single thought renders inline with no header to click.</summary>
    private async void ActivityHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button header && header.Tag is ChatMessage message)
        {
            await DisclosureAnimation.ToggleAsync(
                header,
                DisclosureAnimation.FindContentAfter(header),
                message.IsActivityExpanded,
                message.ToggleActivityExpanded);
        }
    }
}
