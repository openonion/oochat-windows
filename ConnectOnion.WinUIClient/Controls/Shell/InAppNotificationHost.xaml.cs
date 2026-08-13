using System;
using System.Collections.Generic;
using CommunityToolkit.WinUI.Animations;
using ConnectOnion.WinUIClient.Models.Notifications;
using ConnectOnion.WinUIClient.Services;
using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// A window-local overlay that stacks lightweight, non-blocking in-app toasts in
/// the top-right corner. Cards auto-dismiss (approvals linger longer), can be
/// dismissed with a close button, and navigate to their conversation on click.
/// Never uses a modal dialog, so it can't interrupt the user's typing.
/// </summary>
public sealed partial class InAppNotificationHost : UserControl
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ApprovalLifetime = TimeSpan.FromSeconds(12);
    private const int MaxVisible = 4;

    // Cards mid-exit-animation, so a second dismiss (timer + click) doesn't double-animate/remove.
    private readonly HashSet<Border> _dismissing = new();

    // Each live card's auto-dismiss timer, so Shutdown can stop every one of them. A card can
    // leave the stack before its timer fires (eviction by the MaxVisible cap), so the timer is
    // owned here rather than only by the closure that created it.
    private readonly Dictionary<Border, DispatcherTimer> _timers = new();

    // Set once the hosting window is closing: no new cards, and no animation continuation may
    // touch the visual tree after this point.
    private bool _shutdown;

    public InAppNotificationHost() => InitializeComponent();

    public void ShowToast(InAppToastModel toast)
    {
        if (_shutdown) return;

        Services.Notifications.NotificationLog.Info($"InAppNotificationHost.ShowToast: rendering '{toast.Title}'");

        // Cap the stack so a burst can't fill the window.
        while (ToastStack.Children.Count >= MaxVisible)
        {
            if (ToastStack.Children[0] is Border evicted) StopTimer(evicted);
            ToastStack.Children.RemoveAt(0);
        }

        ToastStack.Children.Add(BuildCard(toast));
    }

    /// <summary>
    /// Called when the hosting window closes. Auto-dismiss timers and the exit animation's
    /// continuation both resume on the dispatcher, which keeps pumping for a moment after
    /// <c>Window.Closed</c> — letting either run against a tree the framework is tearing down
    /// is an access violation inside Microsoft.UI.Xaml.dll, not a catchable exception. Drop
    /// the cards synchronously instead.
    /// </summary>
    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;

        foreach (var timer in _timers.Values) timer.Stop();
        _timers.Clear();
        _dismissing.Clear();
        ToastStack.Children.Clear();
    }

    private Border BuildCard(InAppToastModel toast)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 8, 10),
            Background = Brush("SurfaceElevatedBrush"),
            BorderBrush = Brush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
        };

        // Entrance: fade + slide down from above as the card enters the stack. Implicit show
        // animations play automatically when the element is added to the visual tree.
        Implicit.SetShowAnimations(card, new ImplicitAnimationSet
        {
            new OpacityAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(220) },
            new TranslationAnimation { From = "0,-16,0", To = "0,0,0", Duration = TimeSpan.FromMilliseconds(220) },
        });

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FluentIcon
        {
            Icon = IconFor(toast.Type),
            IconVariant = IconVariant.Regular,
            IconSize = IconSize.Size20,
            Foreground = Brush(BrushKeyFor(toast.Type)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        Grid.SetColumn(icon, 0);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        text.Children.Add(new TextBlock
        {
            Text = toast.Title,
            Style = Application.Current.Resources["ProductBodyStrongTextStyle"] as Style,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = toast.Body,
            Style = Application.Current.Resources["ProductCaptionTextStyle"] as Style,
            Foreground = Brush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var close = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Content = new FluentIcon { Icon = Icon.Dismiss, IconVariant = IconVariant.Regular, IconSize = IconSize.Size12, Foreground = Brush("TextSecondaryBrush") },
        };
        Grid.SetColumn(close, 2);

        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(close);
        card.Child = grid;

        var timer = new DispatcherTimer
        {
            Interval = toast.Type == NotificationType.ApprovalRequired ? ApprovalLifetime : DefaultLifetime,
        };
        timer.Tick += (_, _) => Remove(card);

        close.Click += (_, _) => Remove(card);

        card.Tapped += (_, e) =>
        {
            e.Handled = true;
            Remove(card);
            try { AppServices.Navigation.OpenConversation(toast.AgentId, toast.ConversationId); }
            catch { /* best-effort navigation on click */ }
        };

        _timers[card] = timer;
        timer.Start();
        return card;
    }

    private void StopTimer(Border card)
    {
        if (!_timers.Remove(card, out var timer)) return;
        timer.Stop();
    }

    // Exit: fade + slide right, then drop the card once the animation completes. The _dismissing
    // guard makes a second trigger (auto-dismiss timer racing the close button/tap) a no-op.
    private async void Remove(Border card)
    {
        if (_shutdown || !ToastStack.Children.Contains(card) || !_dismissing.Add(card)) return;

        StopTimer(card);
        card.IsHitTestVisible = false;
        await AnimationBuilder.Create()
            .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(160))
            .Translation(Axis.X, to: 16, duration: TimeSpan.FromMilliseconds(160))
            .StartAsync(card);

        // The window may have closed while the animation ran; Shutdown already emptied the
        // stack, and touching it now would run against a half-torn-down tree.
        if (_shutdown) return;

        ToastStack.Children.Remove(card);
        _dismissing.Remove(card);
    }

    private static Icon IconFor(NotificationType type) => type switch
    {
        NotificationType.AgentReply => Icon.Chat,
        NotificationType.TaskCompleted => Icon.CheckmarkCircle,
        NotificationType.ApprovalRequired => Icon.ShieldTask,
        NotificationType.ConnectionLost => Icon.PlugDisconnected,
        _ => Icon.ErrorCircle,
    };

    private static string BrushKeyFor(NotificationType type) => type switch
    {
        NotificationType.TaskCompleted => "SuccessBrush",
        NotificationType.ApprovalRequired => "WarningBrush",
        NotificationType.ConnectionLost => "DangerBrush",
        NotificationType.Error => "DangerBrush",
        _ => "BrandPrimaryBrush",
    };

    private static Brush Brush(string key)
        => (Brush)Application.Current.Resources[key];
}
