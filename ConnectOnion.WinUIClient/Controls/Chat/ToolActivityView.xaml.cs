using System;
using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// The expandable tool-execution card in the transcript: a header summarizing the turn's tool
/// activity, over a timeline of individually expandable steps.
///
/// Two levels of disclosure, handled differently on purpose. Expanding the *card* changes the
/// height of a chat bubble, so it has to preserve scroll position (see
/// <see cref="Header_Click"/>); expanding a *step* happens inside an already-expanded card and
/// does not.
/// </summary>
public sealed partial class ToolActivityView : UserControl
{
    public static readonly DependencyProperty ActivityProperty = DependencyProperty.Register(
        nameof(Activity), typeof(ToolActivityViewModel), typeof(ToolActivityView), new PropertyMetadata(null));

    public ToolActivityViewModel Activity
    {
        get => (ToolActivityViewModel)GetValue(ActivityProperty);
        set => SetValue(ActivityProperty, value);
    }

    public ToolActivityView() => InitializeComponent();

    private async void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button header || Activity is null) return;

        // Save the ListView's scroll offset before the layout changes, so the
        // expansion of the timeline never shifts the header relative to content
        // above it in the chat transcript.
        var scroller = FindAncestorScrollViewer(this);
        var savedOffset = scroller?.VerticalOffset;

        // TimelinePanel by name, not FindContentAfter: that helper takes the header's first
        // sibling, and the collapsed-only error line now sits between the two. Animating it
        // instead left it stranded at opacity 0 — a card toggled twice lost its error line for
        // good — and the timeline stopped animating at all.
        await DisclosureAnimation.ToggleAsync(
            header,
            TimelinePanel,
            Activity.IsExpanded,
            Activity.ToggleExpanded);

        // Restore the scroll offset after the layout pass settles, preventing
        // the ListView's internal ScrollViewer from adjusting the viewport when
        // the timeline item height changes.
        // Low priority so this runs after the framework's own layout pass — restoring the offset
        // synchronously here would be immediately overwritten by the ScrollViewer reacting to
        // the new item height. The `> 0` guard skips the restore when the list was at the top,
        // where there is nothing to correct.
        if (savedOffset is > 0 && scroller is not null)
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => scroller.ChangeView(null, savedOffset.Value, null, disableAnimation: true));
        }
    }

    private async void Step_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button header || header.Tag is not ToolStepViewModel step) return;
        await DisclosureAnimation.ToggleAsync(
            header,
            DisclosureAnimation.FindContentAfter(header),
            step.IsExpanded,
            step.ToggleExpanded);
    }

    /// <summary>Copies a step's full raw log to the clipboard and flashes "Copied" on the button.
    /// The wrap/enlarge toggles are pure state (commands on the step); copy lives here because the
    /// clipboard is a UI concern the WinUI-free model can't reach.</summary>
    private async void LogCopy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ToolStepViewModel step) return;
        var text = step.DetailText;
        if (string.IsNullOrEmpty(text)) return;

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);

        // "Copied" feedback instead of a blocking dialog, cleared after a short delay. The step is
        // a plain model object, so setting this after the await touches no visual tree and is safe
        // even if the row was recycled or the window closed meanwhile.
        step.JustCopied = true;
        await System.Threading.Tasks.Task.Delay(1500);
        step.JustCopied = false;
    }

    private async void InvocationCopy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ToolStepViewModel { HasInvocation: true } step)
            return;

        var package = new DataPackage();
        package.SetText(step.InvocationDisplayText);
        Clipboard.SetContent(package);
        step.JustCopied = true;
        await System.Threading.Tasks.Task.Delay(1200);
        step.JustCopied = false;
    }

    private async void InvocationOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ToolStepViewModel { CanOpenInvocation: true } step
            || !Uri.TryCreate(step.InvocationText, UriKind.Absolute, out var uri))
        {
            return;
        }

        await Services.AppServices.UriLauncher.LaunchAsync(uri);
    }

    /// <summary>Default log height cap; must match <see cref="ToolStepViewModel.LogMaxHeight"/>'s
    /// un-enlarged value. The Expand button only appears when the rendered log is taller than this,
    /// so a short log that already fits shows no pointless Expand.</summary>
    private const double DefaultLogMaxHeight = 280;

    /// <summary>Tracks whether a step's rendered log overflows the default cap. The log sits inside
    /// a vertically-scrolling ScrollViewer, so its measured height is the full content height
    /// regardless of the cap — comparing it to the default tells us whether enlarging reveals more.
    /// Updates as the log streams (each growth re-fires SizeChanged) and on a wrap toggle.</summary>
    /// <remarks>
    /// Reads the step off <c>Tag</c>, not <c>DataContext</c>. A row's DataContext is only assigned
    /// when its DataTemplate carries no compiled binding — the moment the template uses x:Bind the
    /// framework calls ProcessBindings instead and leaves DataContext null, which would silently
    /// stop this matching and leave <see cref="ToolStepViewModel.CanEnlargeLog"/> permanently false
    /// (the Expand/Collapse action would simply never appear). That is the same trap that cost the
    /// sidebar its hover state; every other handler in this file already reads Tag.
    /// </remarks>
    private void Log_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ToolStepViewModel step)
            step.CanEnlargeLog = e.NewSize.Height > DefaultLogMaxHeight;
    }

    private void Header_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (HeaderTitle is not null)
            HeaderTitle.Foreground = ResolveBrush("TextPrimaryBrush");
        if (HeaderChevron is not null)
            HeaderChevron.Opacity = 1;
    }

    private void Header_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (HeaderTitle is not null)
            HeaderTitle.Foreground = ResolveBrush("TextSecondaryBrush");
        if (HeaderChevron is not null)
            HeaderChevron.Opacity = 0.75;
    }

    // A step's hover feedback is the name lifting to the primary foreground and its duration
    // fading up to full strength — and nothing else. The card header still reveals its chevron on
    // hover; an individual step does not — it sits inside an already-opened card, where a per-row
    // icon appearing under the pointer was more motion than the affordance was worth.
    private void StepItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement root) return;
        if (root.FindName("StepNameText") is TextBlock nameText)
            nameText.Foreground = ResolveBrush("TextPrimaryBrush");
        if (root.FindName("StepDurationText") is TextBlock durationText)
            durationText.Opacity = 1;
    }

    private void StepItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement root) return;
        if (root.FindName("StepNameText") is TextBlock nameText)
            nameText.Foreground = ResolveBrush("TextSecondaryBrush");
        if (root.FindName("StepDurationText") is TextBlock durationText)
            durationText.Opacity = 0.5;
    }

    private static Brush ResolveBrush(string key)
        => Presentation.ThemeBrushResolver.Resolve(key);

    /// <summary>Walks up the visual tree to find the ListView's internal
    /// ScrollViewer. Needed so we can save/restore the scroll offset around
    /// expand operations, preventing viewport shifts when item height changes.</summary>
    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer sv) return sv;
        }
        return null;
    }
}
