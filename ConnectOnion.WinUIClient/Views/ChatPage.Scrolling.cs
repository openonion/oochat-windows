using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace ConnectOnion.WinUIClient.Views;

/// <summary>
/// <see cref="ChatPage"/>: scroll position, the stick-to-bottom behaviour, the reading-column
/// inset, and the load overlay that hides list virtualization while a conversation is realizing.
/// <para>This is the most timing-sensitive code on the page: it waits on container realization
/// and frame ticks rather than assuming layout has settled, which is why it is worth keeping
/// apart from the conversation lifecycle.</para>
/// </summary>
public sealed partial class ChatPage
{
    private void ChatRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Centre a fixed-width reading column instead of letting the thread span the
        // whole window. Left-aligned agent turns and right-aligned user turns are
        // ~1200px apart on a maximised window otherwise, which stops them reading as
        // one conversation. The inset never drops below the edge margin, so a narrow
        // window degrades to the old flush-to-edge behaviour rather than going negative.
        var edgeInset = e.NewSize.Width < 640 ? 20 : 52;
        var centringInset = (e.NewSize.Width - ChatColumnMaxWidth) / 2;
        var horizontalInset = Math.Max(edgeInset, centringInset);
        MessageList.Padding = new Thickness(horizontalInset, 14, horizontalInset, 26);
    }

    private async System.Threading.Tasks.Task SendAsync(string text, IReadOnlyList<PendingAttachment>? attachments = null)
    {
        var hasAttachments = attachments is { Count: > 0 };
        if ((string.IsNullOrWhiteSpace(text) && !hasAttachments) || !Vm.CanSend) return;

        await Vm.SendAsync(text, attachments);
        QueueScrollToEnd(force: true);
        // CanSend/IsProcessing propagate back through a queued PropertyChanged callback. Focusing
        // synchronously here can therefore target a TextBox that is disabled for one more
        // dispatcher turn, and WinUI silently drops that focus request. Queue both the final
        // enabled state and focus restoration together so keyboard users can continue typing.
        DispatcherQueue.TryEnqueue(() =>
        {
            Composer.CanSubmit = Vm.CanSend;
            if (Vm.CanSend) Composer.FocusInput();
        });
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isLoadingConversation) return;
        QueueFindRefresh();

        // Hide the empty state once a real message appears.
        if (Vm.Messages.Count > 0 && EmptyStatePanel.Visibility == Visibility.Visible)
            EmptyStatePanel.Visibility = Visibility.Collapsed;

        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace))
        {
            return;
        }

        if (!_stickToBottom)
        {
            ScrollToLatestButton.Visibility = Visibility.Visible;
        }

        QueueScrollToEnd(force: false);
    }

    private void QueueScrollToEnd(bool force)
    {
        if (Vm.Messages.Count == 0) return;
        if (!force && !_stickToBottom) return;
        if (_isAutoScrollQueued) return;

        _isAutoScrollQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _isAutoScrollQueued = false;
            ScrollToEnd();
        });
    }

    /// <summary>
    /// Waits for the ListView to materialize enough virtualized item containers,
    /// then removes the loading overlay and restores the scroll position.
    /// Uses ContainerContentChanging for precise detection; falls back to a
    /// timeout so the overlay never gets stuck.
    /// </summary>
    private async System.Threading.Tasks.Task DismissOverlayWhenItemsReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Vm.Messages.Count == 0)
        {
            // Deliberately no firstConversationRendered mark: there were no bubbles to render,
            // so claiming the milestone would let the benchmark's -RequireConversation gate pass
            // against an empty fixture and time a conversation restore that never happened.
            LoadingOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        // A short chat realizes in a fraction of a second; a long, multi-round one
        // needs longer — both to materialize its containers and, after the scroll
        // restore below, to re-virtualize the fresh (and heavy: markdown, tool
        // timelines, images) bubbles the restore exposes near the bottom. Use one
        // budget that scales with history for both waits instead of a flat timeout.
        try
        {
            var budget = OverlayReadyBudget();

            // First wait: enough top-of-list containers to consider the viewport painted
            // (typical viewport holds 8–15 items), bounded by the budget so the overlay
            // never sticks if items are zero-height/collapsed.
            await WaitForContainersRealizedAsync(
                Math.Min(Vm.Messages.Count, 12), budget, cancellationToken);

            // One frame so the RichTextBlock/Markdown content inside each realized
            // container can finish its first internal layout pass.
            await NextFrameAsync(cancellationToken);

            // Restore scroll position FIRST (while overlay is still visible),
            // then hide the overlay — this way the scroll jump is invisible.
            // The target is kept so the wait below can verify the view actually got there,
            // rather than only listening for an event that may never arrive.
            double? restoreTarget = null;
            if (Vm.SessionId is { } sid
                && ScrollPositions.TryRemove(sid, out var offset)
                && _messageScrollViewer is not null)
            {
                restoreTarget = offset;
                _messageScrollViewer.ChangeView(null, offset, null, disableAnimation: true);
            }
            else
            {
                QueueScrollToEnd(force: true);
            }

            // Second wait: let the scroll actually land. ChangeView is asynchronous even with
            // disableAnimation — it schedules the view change rather than applying it — so
            // measuring container churn immediately after it would sample the frames *before*
            // the re-virtualization it causes has begun. That is what lifted the overlay early
            // on longer conversations: the first frame was quiet simply because nothing had
            // started yet.
            await WaitForScrollSettledAsync(restoreTarget, budget, cancellationToken);

            // Third wait: the restore re-virtualizes a new set of bubbles at the landing point.
            // Hold the overlay until that churn goes quiet. Several consecutive quiet frames,
            // not one: realization arrives in bursts with gaps between them, and a single quiet
            // frame is as likely to be a gap mid-burst as the end of the work.
            await WaitForContainerChurnToSettleAsync(
                budget, requiredQuietFrames: 3, cancellationToken: cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            LoadingOverlay.Visibility = Visibility.Collapsed;
            Diagnostics.StartupTelemetry.Mark(Diagnostics.StartupPhases.FirstConversationRendered);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Navigation owns cancellation; the detached page must not touch its old tree.
        }
        catch
        {
            // Same reasoning as the empty case above — the overlay comes down so the user is not
            // stuck behind it, but a restore that threw is not a rendered conversation and must
            // not be recorded as one.
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// How long the loading overlay may stay up before it is force-dismissed, scaled with
    /// conversation length. Longer histories carry more (and heavier) bubbles to lay out and
    /// a scroll-restore that re-virtualizes the bottom of the list, so they need more time
    /// before the overlay can lift without revealing a half-populated viewport. Clamped so a
    /// short chat stays snappy and a pathological one can never freeze the page.
    /// </summary>
    /// <remarks>
    /// This is a <b>ceiling, not a wait</b> — both waits it bounds exit as soon as the viewport
    /// is genuinely settled, so raising it costs nothing on conversations that settle quickly and
    /// only buys headroom for the ones that do not. The ceiling was previously reached by any
    /// conversation over ~140 messages, which meant the histories most likely to need extra time
    /// were the ones guaranteed not to get it.
    /// </remarks>
    private TimeSpan OverlayReadyBudget()
        => TimeSpan.FromMilliseconds(Math.Clamp(400 + Vm.Messages.Count * 15, 500, 4000));

    /// <summary>Completes once <paramref name="threshold"/> distinct item containers have been
    /// realized, or the timeout elapses — whichever comes first.</summary>
    private async System.Threading.Tasks.Task WaitForContainersRealizedAsync(
        int threshold, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var realized = new HashSet<int>();
        var tcs = new TaskCompletionSource<bool>();

        TypedEventHandler<ListViewBase, ContainerContentChangingEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (args.ItemIndex >= 0)
                realized.Add(args.ItemIndex);

            if (realized.Count < threshold) return;

            MessageList.ContainerContentChanging -= handler;
            tcs.TrySetResult(true);
        };

        MessageList.ContainerContentChanging += handler;
        try
        {
            await Task.WhenAny(tcs.Task, Task.Delay(timeout, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally { MessageList.ContainerContentChanging -= handler; }
    }

    /// <summary>
    /// Waits until container realization has been idle for <paramref name="requiredQuietFrames"/>
    /// consecutive dispatcher frames, bounded by <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// More than one quiet frame is required because realization arrives in bursts — a run of
    /// containers, a gap while their content lays out, then another run. Treating the first gap
    /// as "finished" lifted the overlay in the middle of the work, which is precisely what a
    /// longer conversation has more of.
    /// </remarks>
    private async System.Threading.Tasks.Task WaitForContainerChurnToSettleAsync(
        TimeSpan timeout, int requiredQuietFrames, CancellationToken cancellationToken)
    {
        var churned = false;
        TypedEventHandler<ListViewBase, ContainerContentChangingEventArgs> handler = (_, _) => churned = true;
        MessageList.ContainerContentChanging += handler;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var quietFrames = 0;
            while (quietFrames < requiredQuietFrames && sw.Elapsed < timeout)
            {
                churned = false;
                await NextFrameAsync(cancellationToken);
                // A frame that realized anything resets the run — the count is of *consecutive*
                // quiet frames, not of quiet frames seen.
                quietFrames = churned ? 0 : quietFrames + 1;
            }
        }
        finally { MessageList.ContainerContentChanging -= handler; }
    }

    /// <summary>
    /// Waits for the pending scroll to actually take effect, bounded by <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// <para><c>ChangeView</c> returns before the view has moved even with
    /// <c>disableAnimation: true</c>; it queues the change. Until it lands, the re-virtualization
    /// it causes has not started, so the churn check that follows would sample quiet frames and
    /// lift the overlay over a half-populated viewport.</para>
    /// <para><b>Position is the signal, not just the event.</b> This used to listen only for
    /// <c>ViewChanged</c> and give up after a fixed four frames — which meant that whenever the
    /// queued change needed a fifth (a long history, a slow frame, a machine under load) the wait
    /// returned reporting success while the view had not moved at all. That is the intermittency:
    /// the same conversation settles in time on one open and not on the next. Checking the offset
    /// directly also covers the case no event can: a view already at the requested position never
    /// raises <c>ViewChanged</c>.</para>
    /// <para>Bounded by the caller's overlay budget rather than a frame count, because the budget
    /// is the real answer to "how long may this page withhold itself"; the position check keeps
    /// the fast cases fast, so a larger bound costs nothing when there is nothing to wait for.</para>
    /// </remarks>
    private async System.Threading.Tasks.Task WaitForScrollSettledAsync(
        double? targetOffset, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // One frame unconditionally. The scroll may still be sitting in the dispatcher queue —
        // QueueScrollToEnd defers the ChangeView to a later tick — so on the current frame there
        // is nothing to observe and the position check below would read the pre-scroll offset.
        await NextFrameAsync(cancellationToken);

        if (_messageScrollViewer is null) return;

        var settled = false;
        EventHandler<ScrollViewerViewChangedEventArgs> handler = (_, args) =>
        {
            if (!args.IsIntermediate) settled = true;
        };

        _messageScrollViewer.ViewChanged += handler;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!settled && sw.Elapsed < timeout && !IsAtScrollTarget(targetOffset))
            {
                await NextFrameAsync(cancellationToken);
            }
        }
        finally { _messageScrollViewer.ViewChanged -= handler; }
    }

    /// <summary>
    /// Whether the transcript is already where the restore asked it to be.
    /// <paramref name="targetOffset"/> null means "the bottom", which is where a conversation
    /// opens when it has no remembered position.
    /// </summary>
    private bool IsAtScrollTarget(double? targetOffset)
    {
        if (_messageScrollViewer is null) return true;

        // Sub-pixel tolerance: ChangeView clamps the request to ScrollableHeight and the result
        // is not bit-identical to what was asked for.
        const double epsilon = 0.5;
        var offset = _messageScrollViewer.VerticalOffset;
        var scrollable = _messageScrollViewer.ScrollableHeight;

        if (targetOffset is { } target)
        {
            // Clamped the same way ChangeView clamps it, so a remembered offset past the end of a
            // now-shorter conversation still counts as reached instead of waiting out the budget.
            return Math.Abs(offset - Math.Min(target, scrollable)) <= epsilon;
        }

        // A conversation short enough not to scroll is already at its bottom.
        return scrollable <= epsilon || offset >= scrollable - epsilon;
    }

    /// <summary>Yields until the next low-priority dispatcher tick, i.e. after the current
    /// layout/render pass — one "frame".</summary>
    private System.Threading.Tasks.Task NextFrameAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        if (!DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    registration.Dispose();
                    tcs.TrySetResult();
                }))
        {
            registration.Dispose();
            tcs.TrySetResult();
        }
        return tcs.Task;
    }
    private void JumpToPending_Click(object sender, RoutedEventArgs e) => ScrollToPendingDecision();

    /// <summary>The keyboard route to the pending decision. Returns false when nothing is blocking,
    /// so the shell can leave the key press unhandled rather than swallowing the chord.</summary>
    public bool GoToPendingDecision()
    {
        if (Vm.PendingInteractive is null) return false;
        ScrollToPendingDecision();
        return true;
    }

    /// <summary>Brings the card the agent is blocked on into view and puts keyboard focus on it.
    ///
    /// <para>Focus, not just scroll. A keyboard or screen-reader user reaching a decision by
    /// scrolling still has to Tab through every bubble above it, and with a turn's worth of tool
    /// rows on screen that is a long way; the whole reason this action exists is that the card is
    /// not where the user is.</para>
    ///
    /// <para><c>ScrollIntoView</c> rather than a computed offset: the row may not be realized at
    /// all (the list is virtualized), so there is no element to measure until the list has put one
    /// there. The focus attempt therefore waits a frame for realization, and simply does nothing
    /// if the container still is not there — the scroll has happened either way, which is the part
    /// that matters.</para>
    ///
    /// <para><b>Only ever runs from a user action</b> — the pending bar's button or Ctrl+Shift+D.
    /// Nothing calls it when a decision arrives: moving the viewport, or the focus, on someone's
    /// behalf interrupts whatever they were reading, and the card is not going anywhere. Taking
    /// focus here is right precisely because they asked to go there.</para>
    /// </summary>
    private async void ScrollToPendingDecision()
    {
        if (Vm.PendingInteractive is not { } pending) return;

        // An approval renders inside its owning tool-activity card, and a file-change ask_user
        // inside its diff — in both cases the message that carries the decision is not the row
        // that draws it, so scroll to whichever row is actually on the transcript.
        var target = ResolveTranscriptRow(pending);
        if (target is null) return;

        // The user asked to be at this card, so stop following the tail — being dragged back down
        // would undo the jump. MessageScrollViewer_ViewChanged re-arms the flag on its own once
        // they scroll back near the bottom.
        _stickToBottom = false;
        MessageList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);

        try
        {
            await NextFrameAsync(_pageLifetimeCts.Token);
            if (MessageList.ContainerFromItem(target) is ListViewItem container)
                container.Focus(FocusState.Programmatic);
        }
        catch (OperationCanceledException) when (_pageLifetimeCts.IsCancellationRequested)
        {
        }
    }

    /// <summary>The row that actually renders a decision. Hidden implementation rows (an approval,
    /// or an ask_user still hosted by its diff) resolve to the card that draws them.</summary>
    private ChatMessage? ResolveTranscriptRow(ChatMessage message)
    {
        if (message.IsTranscriptRowVisible) return message;
        if (message.RelatedDiffPreview is { IsTranscriptRowVisible: true } diff) return diff;
        if (message.OwnerToolActivity is { } owner)
        {
            return Vm.Messages.FirstOrDefault(
                candidate => ReferenceEquals(candidate.ToolActivity, owner));
        }
        return null;
    }

    private void ScrollToLatest_Click(object sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        ScrollToLatestButton.Visibility = Visibility.Collapsed;
        ScrollToEnd(animate: true);
    }

    private void ScrollToEnd(bool animate = false)
    {
        if (Vm.Messages.Count == 0) return;

        _stickToBottom = true;
        ScrollToLatestButton.Visibility = Visibility.Collapsed;
        _messageScrollViewer ??= FindDescendant<ScrollViewer>(MessageList);
        if (_messageScrollViewer is not null)
        {
            _messageScrollViewer.ChangeView(null, _messageScrollViewer.ScrollableHeight, null, disableAnimation: !animate);
            return;
        }

        MessageList.ScrollIntoView(Vm.Messages[^1]);
    }

    private async void MessageScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_messageScrollViewer is null) return;

        var distanceFromBottom = _messageScrollViewer.ScrollableHeight - _messageScrollViewer.VerticalOffset;
        _stickToBottom = distanceFromBottom < 96;
        ScrollToLatestButton.Visibility = _stickToBottom ? Visibility.Collapsed : Visibility.Visible;

        // Keep the viewport anchored while an older page is prepended. Waiting until the user is
        // near the top gives the database enough time to load before the boundary becomes visible,
        // while the VM guard coalesces the several ViewChanged events one gesture produces.
        if (_isLoadingConversation
            || _olderHistoryUiLoadActive
            || !Vm.HasMoreHistory
            || _messageScrollViewer.VerticalOffset > 180)
        {
            return;
        }

        _olderHistoryUiLoadActive = true;
        var previousOffset = _messageScrollViewer.VerticalOffset;
        var previousScrollableHeight = _messageScrollViewer.ScrollableHeight;
        try
        {
            var added = await Vm.LoadOlderMessagesAsync(_pageLifetimeCts.Token);
            if (added == 0 || _messageScrollViewer is null) return;

            await NextFrameAsync(_pageLifetimeCts.Token);
            var addedExtent = Math.Max(0, _messageScrollViewer.ScrollableHeight - previousScrollableHeight);
            _messageScrollViewer.ChangeView(
                null,
                previousOffset + addedExtent,
                null,
                disableAnimation: true);
        }
        catch (OperationCanceledException) when (_pageLifetimeCts.IsCancellationRequested)
        {
        }
        finally
        {
            _olderHistoryUiLoadActive = false;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }

        return null;
    }
}
