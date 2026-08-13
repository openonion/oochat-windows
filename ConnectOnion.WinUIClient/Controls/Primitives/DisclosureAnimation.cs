using System;
using System.Numerics;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>Shared short transition for transcript disclosure rows. Closing finishes its fade
/// before Visibility collapses; opening makes the content measurable first, then fades it in.
/// The header is disabled for the 140 ms transition so rapid clicks cannot invert model state.</summary>
internal static class DisclosureAnimation
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(140);
    /// <summary>Honours the OS "show animations" accessibility setting — when it is off, every
    /// toggle below becomes an instant state change. Read once at startup rather than per
    /// toggle: it changes rarely, and a stale value costs at most a transition the user
    /// sees until the app restarts.</summary>
    private static readonly bool AnimationsEnabled = new UISettings().AnimationsEnabled;

    /// <summary>Finds the element a disclosure header expands, by convention: the next
    /// <see cref="FrameworkElement"/> sibling in the same panel. Positional rather than named
    /// so one helper serves every disclosure row in the app — the cost is that reordering a
    /// panel's children silently repoints the animation, so keep header and content adjacent.</summary>
    public static FrameworkElement? FindContentAfter(Button header)
    {
        if (header.Parent is not Panel parent) return null;

        var index = parent.Children.IndexOf(header);
        for (var i = index + 1; i < parent.Children.Count; i++)
        {
            if (parent.Children[i] is FrameworkElement element) return element;
        }
        return null;
    }

    public static async Task ToggleAsync(
        Button header,
        FrameworkElement? content,
        bool isExpanded,
        Action toggle)
    {
        if (content is null || !AnimationsEnabled)
        {
            toggle();
            return;
        }

        // Both the header and the content are made inert for the transition. Without this, a
        // second click mid-animation would run `toggle()` again and land the model in the
        // opposite state from what the finished animation shows.
        header.IsEnabled = false;
        content.IsHitTestVisible = false;
        try
        {
            // The two directions are not symmetric, and the ordering is the whole trick:
            // collapsing fades *then* toggles (so the content is still visible to animate),
            // expanding toggles *then* fades (so the content has a layout slot to animate in).
            if (isExpanded)
            {
                await AnimationBuilder.Create()
                    .Opacity(to: 0, duration: Duration)
                    .Translation(Axis.Y, to: -3, duration: Duration)
                    .StartAsync(content);
                toggle();
            }
            else
            {
                toggle();
                // Seed the "from" state before the frame renders, or the content flashes at
                // full opacity for one frame before the fade-in starts.
                content.Opacity = 0;
                content.Translation = new Vector3(0, -3, 0);
                // Yield so the framework measures/arranges the now-visible content before the
                // animation targets it; animating an element with no layout pass yet is a no-op.
                await Task.Yield();
                await AnimationBuilder.Create()
                    .Opacity(to: 1, duration: Duration)
                    .Translation(Axis.Y, to: 0, duration: Duration)
                    .StartAsync(content);
            }
        }
        finally
        {
            // Reset unconditionally: an animation that was interrupted or never started would
            // otherwise strand the content at opacity 0 and a dead header.
            //
            // KNOWN HAZARD (see the "dispatcher keeps pumping after Window.Closed" notes in
            // CLAUDE.md): this block runs on the far side of an await, so if the window closes
            // mid-transition it touches a visual tree the framework is tearing down. That
            // surfaces as a native access violation inside Microsoft.UI.Xaml.dll that no
            // managed catch sees. A shutdown check here — as InAppNotificationHost.Remove
            // does — would close it.
            content.Opacity = 1;
            content.Translation = Vector3.Zero;
            content.IsHitTestVisible = true;
            header.IsEnabled = true;
        }
    }
}
