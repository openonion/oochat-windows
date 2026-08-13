using System;
using System.Numerics;
using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace ConnectOnion.WinUIClient.Controls;

public sealed partial class InteractiveCard : UserControl
{
    public InteractiveCard()
    {
        InitializeComponent();
        RefreshDisclosure();
        Loaded += OnLoaded;
    }

    public ChatMessage Message
    {
        get => (ChatMessage)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(ChatMessage), typeof(InteractiveCard), new PropertyMetadata(null));

    public string IconGlyph { get => (string)GetValue(IconGlyphProperty); set => SetValue(IconGlyphProperty, value); }
    public static readonly DependencyProperty IconGlyphProperty = DependencyProperty.Register(
        nameof(IconGlyph), typeof(string), typeof(InteractiveCard), new PropertyMetadata("\uE946"));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(InteractiveCard),
        // The title is interpolated into HeaderToggleName, so it has to refresh with it.
        new PropertyMetadata("", OnDisclosureInputChanged));

    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(InteractiveCard), new PropertyMetadata(""));

    public string StatusLabel { get => (string)GetValue(StatusLabelProperty); set => SetValue(StatusLabelProperty, value); }
    public static readonly DependencyProperty StatusLabelProperty = DependencyProperty.Register(
        nameof(StatusLabel), typeof(string), typeof(InteractiveCard), new PropertyMetadata(""));

    public string HeaderMeta { get => (string)GetValue(HeaderMetaProperty); set => SetValue(HeaderMetaProperty, value); }
    public static readonly DependencyProperty HeaderMetaProperty = DependencyProperty.Register(
        nameof(HeaderMeta), typeof(string), typeof(InteractiveCard), new PropertyMetadata(""));

    public bool ShowStatusBadge { get => (bool)GetValue(ShowStatusBadgeProperty); set => SetValue(ShowStatusBadgeProperty, value); }
    public static readonly DependencyProperty ShowStatusBadgeProperty = DependencyProperty.Register(
        nameof(ShowStatusBadge), typeof(bool), typeof(InteractiveCard), new PropertyMetadata(true));

    /// <summary>Whether this card <i>has</i> a body worth showing — a data-driven fact, set by the
    /// specific card. Distinct from <see cref="IsExpanded"/>, which is the user's choice; the body
    /// renders only when both agree (<see cref="IsBodyVisible"/>).</summary>
    public bool ShowBody { get => (bool)GetValue(ShowBodyProperty); set => SetValue(ShowBodyProperty, value); }
    public static readonly DependencyProperty ShowBodyProperty = DependencyProperty.Register(
        nameof(ShowBody), typeof(bool), typeof(InteractiveCard),
        new PropertyMetadata(true, OnDisclosureInputChanged));

    /// <summary>Whether the header itself owns disclosure. Some cards, such as the diff preview,
    /// expose a dedicated footer action backed by their own presentation state; giving those
    /// cards a second header disclosure would create two independent controls for one body.</summary>
    public bool AllowHeaderDisclosure { get => (bool)GetValue(AllowHeaderDisclosureProperty); set => SetValue(AllowHeaderDisclosureProperty, value); }
    public static readonly DependencyProperty AllowHeaderDisclosureProperty = DependencyProperty.Register(
        nameof(AllowHeaderDisclosure), typeof(bool), typeof(InteractiveCard),
        new PropertyMetadata(true, OnDisclosureInputChanged));

    /// <summary>Whether the user has the card open. Starts open — a card that appears folded
    /// would hide a question the agent is blocked on.
    ///
    /// <para>Collapsing hides the body only; the footer stays. That is deliberate: the footer
    /// carries the decision buttons and the state line, so folding a plan you are still being
    /// asked about parks the prose without also hiding Approve/Reject, and a pending card cannot
    /// be made to disappear from the transcript.</para></summary>
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(InteractiveCard),
        new PropertyMetadata(true, OnDisclosureInputChanged));

    /// <summary>The body is on screen: the card has one and the user has not folded it away.</summary>
    public bool IsBodyVisible { get => (bool)GetValue(IsBodyVisibleProperty); private set => SetValue(IsBodyVisibleProperty, value); }
    public static readonly DependencyProperty IsBodyVisibleProperty = DependencyProperty.Register(
        nameof(IsBodyVisible), typeof(bool), typeof(InteractiveCard), new PropertyMetadata(true));

    /// <summary>Whether the header acts as a disclosure control at all. False when there is no
    /// body, in which case the chevron is hidden and the header is not a tab stop — an
    /// affordance that promises nothing is worse than none.</summary>
    public bool CanCollapse { get => (bool)GetValue(CanCollapseProperty); private set => SetValue(CanCollapseProperty, value); }
    public static readonly DependencyProperty CanCollapseProperty = DependencyProperty.Register(
        nameof(CanCollapse), typeof(bool), typeof(InteractiveCard), new PropertyMetadata(true));

    /// <summary>Chevron rotation in degrees clockwise: 0 points down when expanded, 270 points it
    /// right when collapsed. Matches <c>ToolActivityViewModel.ChevronAngle</c> and the sidebar's
    /// agent rows — the same affordance in the same app should turn the same way.</summary>
    public double ChevronAngle { get => (double)GetValue(ChevronAngleProperty); private set => SetValue(ChevronAngleProperty, value); }
    public static readonly DependencyProperty ChevronAngleProperty = DependencyProperty.Register(
        nameof(ChevronAngle), typeof(double), typeof(InteractiveCard), new PropertyMetadata(0d));

    /// <summary>What a screen reader announces for the header button. It has to state the action,
    /// not just the card, because the header is the only thing that reveals or hides the body.</summary>
    public string HeaderToggleName { get => (string)GetValue(HeaderToggleNameProperty); private set => SetValue(HeaderToggleNameProperty, value); }
    public static readonly DependencyProperty HeaderToggleNameProperty = DependencyProperty.Register(
        nameof(HeaderToggleName), typeof(string), typeof(InteractiveCard), new PropertyMetadata(""));

    private static void OnDisclosureInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((InteractiveCard)d).RefreshDisclosure();

    private void RefreshDisclosure()
    {
        CanCollapse = ShowBody && AllowHeaderDisclosure;
        IsBodyVisible = ShowBody && (!AllowHeaderDisclosure || IsExpanded);
        ChevronAngle = IsBodyVisible ? 0 : 270;
        HeaderToggleName = !CanCollapse
            ? AccessibilityName
            : Common.LocalizedStrings.Format(
                IsExpanded ? "InteractiveCardCollapse" : "InteractiveCardExpand",
                IsExpanded ? "Collapse {0}" : "Expand {0}",
                Title);
    }

    private void HeaderToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCollapse) return;
        IsExpanded = !IsExpanded;
    }

    // The chevron rests at low opacity so a settled card reads as content rather than as a
    // control, and lifts under the pointer to say the whole strip is clickable. Same treatment
    // ToolActivityView gives its card header. Skipped when there is nothing to disclose.
    private void HeaderToggle_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (CanCollapse) HeaderChevron.Opacity = 1;
    }

    private void HeaderToggle_PointerExited(object sender, PointerRoutedEventArgs e)
        => HeaderChevron.Opacity = RestingChevronOpacity;

    private const double RestingChevronOpacity = 0.75;

    public bool ShowFooter { get => (bool)GetValue(ShowFooterProperty); set => SetValue(ShowFooterProperty, value); }
    public static readonly DependencyProperty ShowFooterProperty = DependencyProperty.Register(
        nameof(ShowFooter), typeof(bool), typeof(InteractiveCard), new PropertyMetadata(true));

    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(InteractiveCard), new PropertyMetadata(false));

    public InteractiveVisualTone ChromeTone { get => (InteractiveVisualTone)GetValue(ChromeToneProperty); set => SetValue(ChromeToneProperty, value); }
    public static readonly DependencyProperty ChromeToneProperty = DependencyProperty.Register(
        nameof(ChromeTone), typeof(InteractiveVisualTone), typeof(InteractiveCard),
        new PropertyMetadata(InteractiveVisualTone.Neutral, OnChromeToneChanged));

    /// <summary>
    /// Whether the left tone rail is drawn at all.
    ///
    /// <para>The rail exists to signal a tone — amber while an approval is pending, red on a
    /// rejection. <see cref="InteractiveVisualTone.Neutral"/> means there is nothing to signal,
    /// but the tone brush for Neutral is <c>BorderDefaultBrush</c>, so the rail used to paint
    /// itself in exactly the card's own border colour. On a settled card that read as a stray
    /// thick grey edge that matched none of the other three sides and got sliced by the corner
    /// radius — an artifact, not a signal. A settled card already says what it is through its
    /// icon and outcome text.</para>
    /// </summary>
    public bool ShowChromeRail { get => (bool)GetValue(ShowChromeRailProperty); private set => SetValue(ShowChromeRailProperty, value); }
    public static readonly DependencyProperty ShowChromeRailProperty = DependencyProperty.Register(
        nameof(ShowChromeRail), typeof(bool), typeof(InteractiveCard), new PropertyMetadata(false));

    private static void OnChromeToneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((InteractiveCard)d).ShowChromeRail =
            (InteractiveVisualTone)e.NewValue != InteractiveVisualTone.Neutral;

    public InteractiveVisualTone IconTone { get => (InteractiveVisualTone)GetValue(IconToneProperty); set => SetValue(IconToneProperty, value); }
    public static readonly DependencyProperty IconToneProperty = DependencyProperty.Register(
        nameof(IconTone), typeof(InteractiveVisualTone), typeof(InteractiveCard), new PropertyMetadata(InteractiveVisualTone.Neutral));

    public string AccessibilityName { get => (string)GetValue(AccessibilityNameProperty); set => SetValue(AccessibilityNameProperty, value); }
    public static readonly DependencyProperty AccessibilityNameProperty = DependencyProperty.Register(
        nameof(AccessibilityName), typeof(string), typeof(InteractiveCard),
        // Used verbatim as the header's name when the card cannot be collapsed.
        new PropertyMetadata("Interactive card", OnDisclosureInputChanged));

    public object? CardBody { get => GetValue(CardBodyProperty); set => SetValue(CardBodyProperty, value); }
    public static readonly DependencyProperty CardBodyProperty = DependencyProperty.Register(
        nameof(CardBody), typeof(object), typeof(InteractiveCard), new PropertyMetadata(null));

    public object? CardFooter { get => GetValue(CardFooterProperty); set => SetValue(CardFooterProperty, value); }
    public static readonly DependencyProperty CardFooterProperty = DependencyProperty.Register(
        nameof(CardFooter), typeof(object), typeof(InteractiveCard), new PropertyMetadata(null));

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Message is null || Message.HasPlayedInteractiveEntrance) return;
        Message.HasPlayedInteractiveEntrance = true;
        if (!new UISettings().AnimationsEnabled) return;

        CardBorder.Opacity = 0;
        CardBorder.Translation = new Vector3(0, 4, 0);
        CardBorder.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) };
        CardBorder.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(140) };
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            CardBorder.Opacity = 1;
            CardBorder.Translation = Vector3.Zero;
        });
    }
}
