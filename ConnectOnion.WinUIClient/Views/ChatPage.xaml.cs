using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Controls;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace ConnectOnion.WinUIClient.Views;

public sealed partial class ChatPage : Page, IFindHost, IShutdownDisarmable, IDisposable
{
    /// <summary>Width of the centred conversation column; see the ChatColumnWidth resource in App.xaml.</summary>
    private static double ChatColumnMaxWidth => (double)Application.Current.Resources["ChatColumnWidth"];

    /// <summary>Persisted scroll positions keyed by session id, survives page recreation.
    /// Bounded — see <see cref="SaveScrollPosition"/> for why an unrestored entry would
    /// otherwise stay forever.</summary>
    private static readonly ConcurrentDictionary<string, double> ScrollPositions = new();
    private static readonly ConcurrentQueue<string> ScrollPositionOrder = new();
    private const int MaxRememberedScrollPositions = 64;
    private const int HeavyPageReclaimCheckInterval = 8;
    private const long NativeReclaimPrivateBytesThreshold = 320L * 1024 * 1024;
    private static readonly long NativeReclaimCooldownTicks =
        System.Diagnostics.Stopwatch.Frequency * 30;
    private static int _releasedHeavyPages;
    private static int _nativeReclaimScheduled;
    private static long _lastNativeReclaimTimestamp;

    public ChatViewModel Vm { get; } = App.GetService<ChatViewModel>();
    private ScrollViewer? _messageScrollViewer;
    private bool _isLoadingConversation;
    private bool _isAutoScrollQueued;
    private bool _stickToBottom = true;
    private bool _olderHistoryUiLoadActive;
    private ComposerSubmission? _pendingInitialSubmission;
    private MainWindow? _hostWindow;
    private CancellationTokenSource? _loadCts;
    private readonly CancellationTokenSource _pageLifetimeCts = new();
    private int _disposed;

    public ChatPage()
    {
        InitializeComponent();

        // A conversation owns a heavy ListView containing native RichTextBlock/DataTemplate
        // containers. Repointing one page with Messages.Clear() leaves those containers in the
        // ListView recycle pool, so every opened conversation adds another native high-water
        // allocation. A fresh, uncached page lets the previous list and its pool be destroyed.
        NavigationCacheMode = NavigationCacheMode.Disabled;

        Vm.Messages.CollectionChanged += Messages_CollectionChanged;
        Vm.PropertyChanged += Vm_PropertyChanged;
        // All page-to-view-model hooks are paired with removals in Dispose. A conversation
        // switch creates a new page, so keeping any of these hooks would retain the old tree.
        Vm.SendCancelled += Vm_SendCancelled;
        Vm.ConversationChanged += Vm_ConversationChanged;
        _findDebounceTimer.Tick += FindDebounceTimer_Tick;
        ChatRoot.KeyDown += ChatRoot_KeyDown;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Stops everything on this page that could still fire after the window closes. Called from
    /// <c>MainWindow.DetachWindowServices</c> on the real exit path, because <see cref="OnUnloaded"/>
    /// — where these are normally disarmed — is not guaranteed to run on window close.
    ///
    /// <para>Deliberately <i>not</i> a call to <see cref="OnUnloaded"/>: that also cancels the
    /// load, reports viewing state, and persists scroll position, all of which touch services or
    /// start work at the exact moment the app is tearing them down. Disarming is the only part
    /// that is safe — and necessary — here.</para>
    ///
    /// <para><c>Composer.Dispose()</c> is the composer's own synchronous, idempotent teardown: it
    /// stops the recording-elapsed timer, cancels its lifetime token, releases the speech
    /// recognizer and audio graph, and drops the Win2D stroke resources. Its <c>Unloaded</c>
    /// handler is <c>async void</c> and would resume on a dispatcher that may no longer have a
    /// tree to touch, so this path must not rely on it.</para>
    /// </summary>
    public void DisarmForShutdown()
    {
        _findDebounceTimer.Stop();
        StopUiFeedbackLifetime(restoreVisuals: false);
        // Lives on the view model, which outlives this page, so stopping it here is not redundant.
        Vm.StopThinkingTicker();
        DisposeThinkingIndicators(MessageList);
        Composer.Dispose();
    }

    private static void DisposeThinkingIndicators(DependencyObject root)
    {
        for (var index = VisualTreeHelper.GetChildrenCount(root) - 1; index >= 0; index--)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            DisposeThinkingIndicators(child);
            if (child is ThinkingIndicator indicator) indicator.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        DisarmForShutdown();
        _pageLifetimeCts.Cancel();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        Vm.Messages.CollectionChanged -= Messages_CollectionChanged;
        Vm.PropertyChanged -= Vm_PropertyChanged;
        Vm.SendCancelled -= Vm_SendCancelled;
        Vm.ConversationChanged -= Vm_ConversationChanged;
        _findDebounceTimer.Tick -= FindDebounceTimer_Tick;
        ChatRoot.KeyDown -= ChatRoot_KeyDown;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        if (_messageScrollViewer is not null)
            _messageScrollViewer.ViewChanged -= MessageScrollViewer_ViewChanged;

        // Break the ItemsControl -> ObservableCollection -> ChatMessage graph while the old
        // native containers are still reachable. Waiting for a later XAML/COM collection is what
        // made rapid session switches retain several complete transcript trees concurrently.
        MessageList.ItemsSource = null;
        MessageList.ItemTemplateSelector = null;
        Vm.Cleanup();
        _pageLifetimeCts.Dispose();

        // Native transcript trees can be much larger than their wrappers, but a deterministic
        // blocking Gen2 collection every few navigations creates periodic process-wide pauses.
        // Check infrequently and collect only under demonstrated private-byte pressure, with a
        // cooldown and a non-blocking optimized collection.
        var released = Interlocked.Increment(ref _releasedHeavyPages);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref _lastNativeReclaimTimestamp);
        long privateBytes;
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
            privateBytes = process.PrivateMemorySize64;
        var underPressure = released % HeavyPageReclaimCheckInterval == 0
            && privateBytes >= NativeReclaimPrivateBytesThreshold;
        if (underPressure
            && now - last >= NativeReclaimCooldownTicks
            && Interlocked.CompareExchange(ref _lastNativeReclaimTimestamp, now, last) == last
            && Interlocked.Exchange(ref _nativeReclaimScheduled, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem(static _ =>
            {
                try
                {
                    GC.Collect(
                        GC.MaxGeneration,
                        GCCollectionMode.Optimized,
                        blocking: false,
                        compacting: false);
                }
                finally
                {
                    Interlocked.Exchange(ref _nativeReclaimScheduled, 0);
                }
            });
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loadCts?.Cancel();
        StopUiFeedbackLifetime(restoreVisuals: true);
        // No longer viewing this conversation — clear it so notifications resume.
        ReportViewing(active: false);

        // Persist scroll position so returning to this conversation restores it.
        SaveScrollPosition();

        // This page is deliberately uncached: fully sever the view-model/event graph so the
        // ListView and its native element-recycle pool can be reclaimed after navigation.
        Dispose();
    }

    /// <summary>Reports to <c>WindowPresenceService</c> whether this window is
    /// currently showing this conversation, so the notification policy can suppress
    /// notifications the user would already see. The host window is captured while
    /// loaded so it can still be cleared during unload (when XamlRoot may be gone).</summary>
    private void ReportViewing(bool active)
    {
        try
        {
            if (active)
            {
                _hostWindow = MainWindow.FromXamlRoot(XamlRoot);
                if (_hostWindow is not null)
                    AppServices.WindowPresence.SetViewing(_hostWindow, Vm.AgentId, Vm.SessionId);
            }
            else
            {
                if (_hostWindow is not null)
                    AppServices.WindowPresence.SetViewing(_hostWindow, null, null);
                _hostWindow = null;
            }
        }
        catch { /* Best-effort presence reporting. */ }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Vm.CanSend) or nameof(Vm.IsProcessing))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                Composer.CanSubmit = Vm.CanSend;
                Composer.IsBusy = Vm.IsProcessing;
            });
        }

        // Deliberately no auto-scroll when a decision arrives. Moving the viewport for the user is
        // the kind of help that is wrong as often as it is right — it interrupts whatever they were
        // reading, and the transcript is already where they left it. Going to the card is a user
        // action: the pending bar above the composer, or Ctrl+Shift+D.
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var parameter = e.Parameter is ShellNavigationContext context
            ? context.Payload
            : e.Parameter;
        // AgentDetailPage navigates here with a ComposerSubmission (text + any
        // attachments); a plain string is also accepted for robustness against
        // any other caller that only ever had a prompt to pass.
        _pendingInitialSubmission = parameter switch
        {
            ComposerSubmission submission => submission,
            string prompt => new ComposerSubmission(prompt, Array.Empty<PendingAttachment>()),
            _ => null,
        };
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // This callback runs while the old visual tree is still reachable. Dispose the controls
        // that own native resources here rather than waiting for GC/Unloaded, where the
        // composer's audio graph and speech recognizer can already have escaped deterministic
        // teardown.
        SaveScrollPosition();
        ReportViewing(active: false);
        Dispose();
        base.OnNavigatedFrom(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartUiFeedbackLifetime();
        _messageScrollViewer ??= FindDescendant<ScrollViewer>(MessageList);
        if (_messageScrollViewer is not null)
        {
            _messageScrollViewer.ViewChanged -= MessageScrollViewer_ViewChanged;
            _messageScrollViewer.ViewChanged += MessageScrollViewer_ViewChanged;
        }

        await LoadConversationAsync();
    }

    /// <summary>Remembers where the *outgoing* conversation was scrolled to, so coming back to
    /// it lands in the same place. Keyed by session id, consumed (and removed) on restore.
    /// <para>Entries are only removed on restore, and a conversation the user leaves and never
    /// returns to is never restored — so the map is FIFO-capped rather than left to grow one
    /// entry per conversation visited. Losing the oldest offset just means that conversation
    /// opens at the bottom, which is where a chat opens by default anyway.</para></summary>
    private void SaveScrollPosition()
    {
        try
        {
            if (Vm.SessionId is not { } sid || _messageScrollViewer is null) return;

            if (ScrollPositions.TryAdd(sid, _messageScrollViewer.VerticalOffset))
                ScrollPositionOrder.Enqueue(sid);
            else
                ScrollPositions[sid] = _messageScrollViewer.VerticalOffset;

            while (ScrollPositionOrder.Count > MaxRememberedScrollPositions
                && ScrollPositionOrder.TryDequeue(out var oldest))
            {
                ScrollPositions.TryRemove(oldest, out _);
            }
        }
        catch { /* Best-effort scroll persistence. */ }
    }

    private async System.Threading.Tasks.Task LoadConversationAsync()
    {
        _loadCts?.Cancel();
        var loadCts = new CancellationTokenSource();
        _loadCts = loadCts;
        _isLoadingConversation = true;
        try
        {
            await Vm.LoadAsync(loadCts.Token);
        }
        catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // Failed to restore the conversation (e.g. corrupted local
            // storage); the page still loads in an empty state so the user
            // can start a fresh chat.
        }
        finally
        {
            _isLoadingConversation = false;
            if (ReferenceEquals(_loadCts, loadCts)) _loadCts = null;
            loadCts.Dispose();
        }

        // Rapid switching can navigate away and unload this page during the DB load
        // above. Touching the composer/overlay dependency properties or the message list while
        // this page is detached from the visual tree throws E_UNEXPECTED (0x8000FFFF). Bail if we
        // are no longer loaded; the next OnLoaded re-runs against the current selection.
        if (!IsLoaded) return;

        // Tell the notification layer this window is now showing this conversation,
        // so a reply that lands here is suppressed (the user already sees it).
        ReportViewing(active: true);

        // A previous load may have left this panel visible, while history changes
        // raised during LoadAsync are intentionally ignored by
        // Messages_CollectionChanged. Always derive the final state from the
        // conversation that just finished loading instead of only handling the
        // empty case.
        EmptyStatePanel.Visibility = Vm.Messages.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Wait for the ListView to materialize its virtualized item containers
        // before removing the overlay.  A fixed delay cannot account for
        // varying message counts and hardware speed — use ContainerContentChanging
        // to detect when visible items are actually realized.
        _ = DismissOverlayWhenItemsReadyAsync(_pageLifetimeCts.Token);
        Composer.CanSubmit = Vm.CanSend;
        Composer.IsBusy = Vm.IsProcessing;
        Composer.FocusInput();

        var pending = _pendingInitialSubmission;
        if (pending is not null && (!string.IsNullOrWhiteSpace(pending.Text) || pending.Attachments.Count > 0))
        {
            _pendingInitialSubmission = null;
            // The mode picked on AgentDetailPage, applied before the first send so the turn it
            // starts actually runs under it. SetModeAsync is a no-op when the conversation is
            // already in that mode, which is the common case (Safe).
            await Vm.SetModeAsync(pending.Mode);
            await SendAsync(pending.Text, pending.Attachments);
        }
    }

    private async void Composer_SubmitRequested(object? sender, ComposerSubmission submission)
        => await SendAsync(submission.Text, submission.Attachments);

    /// <summary>
    /// One button, two meanings. While the message is still queued it has not reached the agent,
    /// so Stop takes it back outright; once the agent has it, Stop is the cooperative request the
    /// agent may decline. The composer only reports the press — deciding which of the two applies
    /// is the page's job, because only the run's state can answer it.
    /// </summary>
    private async void Composer_StopRequested(object? sender, System.EventArgs e)
    {
        if (Vm.CanCancelSend)
        {
            await Vm.CancelSendAsync();
            return;
        }
        await Vm.StopAsync();
    }

    /// <summary>Puts a taken-back message back on the composer.</summary>
    private async void Vm_SendCancelled(string text, IReadOnlyList<PendingAttachment> attachments)
        => await Composer.RestoreDraftAsync(text, attachments);

    /// <summary>
    /// The page is now showing a different conversation than the one it loaded (New / Edit /
    /// Retry all branch in place, without navigating), so re-report presence against the new id.
    /// Without this the policy keeps matching against the conversation we left, and a turn the
    /// user is watching notifies as though they were somewhere else.
    /// </summary>
    private void Vm_ConversationChanged()
    {
        // Only meaningful while attached: ReportViewing resolves the host window through XamlRoot,
        // and a detached page has none. OnLoaded reports again anyway when it comes back.
        if (IsLoaded) ReportViewing(active: true);
    }

    private async void Composer_ModeChangeRequested(object? sender, string mode)
        => await Vm.SetModeAsync(mode);

    /// <summary>Cycles the same three choices shown by the composer's mode picker.</summary>
    internal Task CycleApprovalModeAsync()
        => Vm.SetModeAsync(AgentModes.Next(Vm.CurrentMode));

    private async void OfflineNotice_RecheckRequested(object? sender, System.EventArgs e)
        => await Vm.RecheckPresenceAsync();

    private async void SubmitOnboard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChatMessage message)
        {
            await Vm.SubmitOnboardInviteCodeAsync(message);
            ScrollToEnd();
        }
    }

    private async void SubmitOnboardPayment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChatMessage message)
        {
            await Vm.SubmitOnboardPaymentAsync(message);
            ScrollToEnd();
        }
    }



}
