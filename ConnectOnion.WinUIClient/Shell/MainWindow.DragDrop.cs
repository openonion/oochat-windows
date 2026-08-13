using ConnectOnion.WinUIClient.Services.Attachments;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace ConnectOnion.WinUIClient;

/// <summary>
/// Window-wide drag feedback. The only real drop target in the app is
/// <see cref="Controls.ChatComposer"/>; everywhere else a dragged file would simply be
/// refused with no explanation. These handlers turn that dead zone into a visible hint
/// pointing at the composer.
/// </summary>
public sealed partial class MainWindow
{
    private void RegisterDragDropHint()
    {
        // Required for the window to be hit-tested during a drag at all — without it the
        // events only fire over descendants that opt in individually.
        RootGrid.AllowDrop = true;

        // handledEventsToo: the composer marks the drag Handled once it claims the payload,
        // and the event still bubbles up here afterwards. A normal XAML handler would stop
        // firing the moment the pointer crossed onto the composer, leaving the hint stuck on
        // screen directly above the drop target it is pointing at.
        RootGrid.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(RootGrid_DragOver), handledEventsToo: true);
        RootGrid.AddHandler(UIElement.DragOverEvent, new DragEventHandler(RootGrid_DragOver), handledEventsToo: true);
        RootGrid.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler(RootGrid_DragLeave), handledEventsToo: true);
        RootGrid.AddHandler(UIElement.DropEvent, new DragEventHandler(RootGrid_Drop), handledEventsToo: true);
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        // Handled means a composer already claimed this drag and is showing its own drop
        // target — repeating "drop it on the composer" over the composer would be noise.
        // Leave its AcceptedOperation alone; overwriting it here would refuse the drop.
        if (e.Handled || !AttachmentDropService.ContainsFiles(e.DataView))
        {
            DragHintPanel.Visibility = Visibility.Collapsed;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.None;
        DragHintPanel.Visibility = Visibility.Visible;
    }

    // Also fires when the pointer merely crosses from the window onto the composer (the
    // composer's own DragLeave bubbles here). That is harmless: the composer's DragOver has
    // already run this frame, so the hint is re-evaluated immediately on the next move.
    private void RootGrid_DragLeave(object sender, DragEventArgs e)
        => DragHintPanel.Visibility = Visibility.Collapsed;

    private void RootGrid_Drop(object sender, DragEventArgs e)
        => DragHintPanel.Visibility = Visibility.Collapsed;
}
