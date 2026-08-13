using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Controls;

public sealed partial class PlanSectionReviewDialog : ContentDialog
{
    public PlanSectionReviewDialog(string markdown, bool isReadOnly = false)
    {
        IsReadOnly = isReadOnly;
        Sections = new ObservableCollection<PlanReviewSectionItem>(
            PlanReviewSections.Parse(markdown)
                .Select(section => new PlanReviewSectionItem(
                    section.Heading, section.Markdown, isReadOnly)));
        InitializeComponent();
        if (isReadOnly)
        {
            PrimaryButtonText = "";
            SecondaryButtonText = "";
            CloseButtonText = Common.LocalizedStrings.Get("CommonClose", "Close");
            DefaultButton = ContentDialogButton.None;
        }
        Opened += OnOpened;
        Closed += OnClosed;
    }

    public bool IsReadOnly { get; }
    public ObservableCollection<PlanReviewSectionItem> Sections { get; }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        ApplyResponsiveSize();
        if (XamlRoot is not null) XamlRoot.Changed += XamlRoot_Changed;
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (XamlRoot is not null) XamlRoot.Changed -= XamlRoot_Changed;
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
        => ApplyResponsiveSize();

    /// <summary>Widest the section list is allowed to get. Must stay below the
    /// <c>ContentDialogMaxWidth</c> override in the XAML minus <see cref="DialogChromeWidth"/>,
    /// or the grid is handed a width the dialog cannot show and the overflow is clipped.</summary>
    private const double MaxContentWidth = 620;

    /// <summary>Horizontal space the dialog spends on itself (padding plus border) around the
    /// content.</summary>
    private const double DialogChromeWidth = 48;

    /// <summary>Taken off the window width before the content is sized, so a narrow window shrinks
    /// the *content* rather than producing a dialog wider than the window it sits in: the dialog's
    /// own chrome, plus a margin so it never runs edge to edge.</summary>
    private const double WindowInset = DialogChromeWidth + 48;

    private void ApplyResponsiveSize()
    {
        if (XamlRoot is not { } root) return;
        ContentGrid.Width = Math.Clamp(root.Size.Width - WindowInset, 320, MaxContentWidth);
        ContentGrid.Height = Math.Clamp(root.Size.Height - 180, 240, 620);
    }

    public string BuildFeedback()
    {
        var entries = Sections
            .Where(section => section.NeedsChanges || !string.IsNullOrWhiteSpace(section.Feedback))
            .Select(section =>
            {
                var feedback = section.Feedback.Trim();
                return feedback.Length == 0
                    ? $"[{section.Heading}] Needs changes."
                    : $"[{section.Heading}] {feedback}";
            });
        return string.Join(Environment.NewLine, entries);
    }

    private void ApproveSection_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PlanReviewSectionItem section)
            section.SetDecision(approved: true);
    }

    private void RequestSectionChanges_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PlanReviewSectionItem section)
            section.SetDecision(approved: false);
    }
}

public sealed partial class PlanReviewSectionItem : ObservableObject
{
    public PlanReviewSectionItem(string heading, string markdown, bool isReadOnly = false)
    {
        Heading = heading;
        Markdown = markdown;
        IsReadOnly = isReadOnly;
        Feedback = "";
    }

    public string Heading { get; set; }
    public string Markdown { get; set; }
    public bool IsReadOnly { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApproveAccessibilityName))]
    [NotifyPropertyChangedFor(nameof(ChangesAccessibilityName))]
    public partial bool IsApproved { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApproveAccessibilityName))]
    [NotifyPropertyChangedFor(nameof(ChangesAccessibilityName))]
    public partial bool NeedsChanges { get; private set; }

    [ObservableProperty]
    public partial string Feedback { get; set; }

    public string ApproveAccessibilityName
        => $"{Heading}: looks good, {(IsApproved ? "selected" : "not selected")}";
    public string ChangesAccessibilityName
        => $"{Heading}: needs changes, {(NeedsChanges ? "selected" : "not selected")}";
    public string FeedbackAccessibilityName => $"Feedback for {Heading}";

    public void SetDecision(bool approved)
    {
        IsApproved = approved;
        NeedsChanges = !approved;
    }
}
