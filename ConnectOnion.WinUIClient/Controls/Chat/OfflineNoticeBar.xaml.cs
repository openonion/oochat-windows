using System;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// Shared "agent is offline" notice card. The host sets layout (alignment,
/// margin, MaxWidth, Visibility) on the control instance and handles
/// <see cref="RecheckRequested"/> to re-probe presence.
/// </summary>
public sealed partial class OfflineNoticeBar : UserControl
{
    /// <summary>Honours the OS "show animations" accessibility setting (Reduce Motion). Read once
    /// as a startup constant, matching <c>DisclosureAnimation</c>/<c>ThinkingIndicator</c>.</summary>
    private static readonly bool AnimationsEnabled = new UISettings().AnimationsEnabled;

    public static readonly DependencyProperty DetailProperty =
        DependencyProperty.Register(
            nameof(Detail),
            typeof(string),
            typeof(OfflineNoticeBar),
            new PropertyMetadata(""));

    public static readonly DependencyProperty CanRecheckProperty =
        DependencyProperty.Register(
            nameof(CanRecheck),
            typeof(bool),
            typeof(OfflineNoticeBar),
            new PropertyMetadata(true));

    public static readonly DependencyProperty IsCheckingProperty =
        DependencyProperty.Register(
            nameof(IsChecking),
            typeof(bool),
            typeof(OfflineNoticeBar),
            new PropertyMetadata(false));

    public static readonly DependencyProperty StatusMessageProperty =
        DependencyProperty.Register(
            nameof(StatusMessage),
            typeof(string),
            typeof(OfflineNoticeBar),
            new PropertyMetadata(""));

    public event EventHandler? RecheckRequested;

    public OfflineNoticeBar()
    {
        InitializeComponent();
        // The host attaches the slide/fade entrance via Implicit.ShowAnimations in its XAML, which
        // is set on this instance after our constructor runs — so Reduce Motion is honoured in
        // Loaded (still collapsed at that point, before the first show fires) by clearing it. The
        // card then simply appears, its meaning unchanged.
        if (!AnimationsEnabled)
            Loaded += (_, _) => ClearValue(Implicit.ShowAnimationsProperty);
    }

    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public bool CanRecheck
    {
        get => (bool)GetValue(CanRecheckProperty);
        set => SetValue(CanRecheckProperty, value);
    }

    /// <summary>True while a reachability probe is in flight — swaps the Recheck
    /// button for a spinner.</summary>
    public bool IsChecking
    {
        get => (bool)GetValue(IsCheckingProperty);
        set => SetValue(IsCheckingProperty, value);
    }

    /// <summary>Optional attention line shown under the detail, e.g. after a
    /// recheck that resolved to still-offline.</summary>
    public string StatusMessage
    {
        get => (string)GetValue(StatusMessageProperty);
        set => SetValue(StatusMessageProperty, value);
    }

    private void Recheck_Click(object sender, RoutedEventArgs e)
        => RecheckRequested?.Invoke(this, EventArgs.Empty);
}
