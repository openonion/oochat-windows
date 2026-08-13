using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// Window-filling, command-palette-style conversation search. It follows the existing settings,
/// shortcuts, and About overlay contract: one MainWindow-owned instance, a dimmed backdrop,
/// live local filtering, Esc/light-dismiss, and focus restoration.
/// </summary>
public sealed partial class SessionSearchOverlay : UserControl, IDisposable
{
    private const int MaxResults = 100;
    private FrameworkElement? _focusReturnTarget;

    /// <summary>Debounce for the transcript search. Stopped in <see cref="Hide"/> — the dispatcher
    /// keeps pumping after the window closes, so an armed timer can tick into a torn-down tree.</summary>
    private readonly DispatcherTimer _contentSearchDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(180),
    };

    private CancellationTokenSource? _contentSearchCts;
    private int _disposed;

    public event EventHandler? CloseRequested;
    public event EventHandler<SessionSearchSelectionEventArgs>? SessionSelected;

    public SessionSearchViewModel Vm { get; } = App.GetService<SessionSearchViewModel>();

    public SessionSearchOverlay()
    {
        InitializeComponent();
        SizeChanged += SessionSearchOverlay_SizeChanged;
        _contentSearchDebounce.Tick += ContentSearchDebounce_Tick;
    }

    private void SessionSearchOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateModalSize(e.NewSize.Width, e.NewSize.Height);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _contentSearchDebounce.Stop();
        _contentSearchDebounce.Tick -= ContentSearchDebounce_Tick;
        SizeChanged -= SessionSearchOverlay_SizeChanged;
        _contentSearchCts?.Cancel();
        _contentSearchCts?.Dispose();
        _contentSearchCts = null;
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Exposes this overlay to UI Automation as a dialog. Without a peer the control is
    /// invisible to UIA entirely — no dialog boundary for a screen reader, and its
    /// AutomationId unreachable from a UI test. See <see cref="ModalOverlayAutomationPeer"/>.</summary>
    protected override Microsoft.UI.Xaml.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        => new ModalOverlayAutomationPeer(this);


    public async Task ShowAsync(FrameworkElement? focusReturnTarget)
    {
        if (IsOpen)
        {
            DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));
            return;
        }

        _focusReturnTarget = focusReturnTarget;
        SearchBox.Text = "";
        Vm.Reset([]);
        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        UpdateModalSize(ActualWidth, ActualHeight);
        DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));

        var items = await LoadRecentItemsAsync();
        if (!IsOpen) return;

        Vm.Reset(items);
        SelectFirstResult();
    }

    public void Hide()
    {
        if (!IsOpen) return;

        // Disarm before the overlay goes away: a tick or a resuming continuation that lands after
        // teardown is exactly the access-violation-in-Microsoft.UI.Xaml.dll shape described in
        // CLAUDE.md, and the dispatcher keeps pumping after Window.Closed.
        _contentSearchDebounce.Stop();
        _contentSearchCts?.Cancel();

        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        _focusReturnTarget?.Focus(FocusState.Programmatic);
        _focusReturnTarget = null;
    }

    private static async Task<IReadOnlyList<SessionSearchItem>> LoadRecentItemsAsync()
    {
        try
        {
            // Names only, and never written back — the thin read skips every agent's /info blob.
            var agentsTask = AppServices.Agents.LoadSummariesAsync();
            var sessionsTask = AppServices.Sessions.LoadRecentAsync(MaxResults);
            await Task.WhenAll(agentsTask, sessionsTask);

            var agentNames = agentsTask.Result.Agents.ToDictionary(
                agent => agent.Id,
                agent => agent.Name,
                StringComparer.Ordinal);

            return sessionsTask.Result
                .Select(session => new SessionSearchItem
                {
                    SessionId = session.Id,
                    AgentId = session.AgentId,
                    Title = session.Title,
                    AgentName = agentNames.TryGetValue(session.AgentId, out var name) ? name : "",
                    UpdatedAt = session.UpdatedAt,
                })
                .ToList();
        }
        catch
        {
            // Search is a convenience surface. A storage refresh failure becomes an honest empty
            // state instead of taking down the shell or showing a modal error above this modal.
            return [];
        }
    }

    private void UpdateModalSize(double width, double height)
    {
        if (width <= 0 || height <= 0) return;

        // Stretch + MaxWidth, so the width is "available minus Margin" as resolved by layout.
        // The removed line computed it from ActualWidth with its own margin constant, which
        // disagreed with the Margin set below and, away from 100% zoom/text scale, measured a
        // different width than the window (FloatingOverlayLayer is scaled).
        ModalContainer.MaxHeight = Math.Max(0, Math.Min(620, height - 64));
        ModalContainer.Margin = new Thickness(20, height >= 640 ? 72 : 24, 20, 20);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _contentSearchCts?.Cancel();
        Vm.SearchText = SearchBox.Text;
        var queryLength = SearchBox.Text.Trim().Length;
        ShortTranscriptSearchHint.Visibility = queryLength is > 0 and < 3
            ? Visibility.Visible
            : Visibility.Collapsed;
        SelectFirstResult();
        // The bounded recent window still filters synchronously; the complete title/agent and
        // transcript search trails behind one shared debounce.
        RestartContentSearchDebounce();
    }

    /// <summary>
    /// Waits out a burst of typing before querying the transcript. 180ms is long enough that a
    /// normal typing run issues one query instead of one per character, and short enough that the
    /// excerpts feel like part of the same interaction.
    /// </summary>
    private void RestartContentSearchDebounce()
    {
        _contentSearchDebounce.Stop();
        if (SearchBox.Text.Trim().Length == 0) return;
        _contentSearchDebounce.Start();
    }

    private async void ContentSearchDebounce_Tick(object? sender, object e)
    {
        _contentSearchDebounce.Stop();

        var query = SearchBox.Text;
        // Every keystroke supersedes the query in flight; the token is what stops a slow earlier
        // result from overwriting a newer one.
        _contentSearchCts?.Cancel();
        _contentSearchCts?.Dispose();
        _contentSearchCts = new CancellationTokenSource();
        var token = _contentSearchCts.Token;

        try
        {
            var titleTask = AppServices.Sessions.SearchByTitleOrAgentAsync(query, MaxResults, token);
            var contentTask = query.Trim().Length >= 3
                ? AppServices.Conversations.SearchMessageContentAsync(query, MaxResults, token)
                : Task.FromResult<IReadOnlyDictionary<string, string>>(
                    new Dictionary<string, string>(StringComparer.Ordinal));
            await Task.WhenAll(titleTask, contentTask).ConfigureAwait(true);

            var titleSessions = await titleTask.ConfigureAwait(true);
            var matches = await contentTask.ConfigureAwait(true);
            var titleIds = titleSessions.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
            var missingIds = matches.Keys.Where(id => !titleIds.Contains(id)).Take(MaxResults).ToList();
            var contentSessions = missingIds.Count == 0
                ? Array.Empty<ConnectOnion.WinUIClient.Models.SessionSummary>()
                : await AppServices.Sessions.LoadSessionsByIdsAsync(missingIds, token).ConfigureAwait(true);

            if (token.IsCancellationRequested || !IsOpen) return;
            var agentNames = (await AppServices.Agents.LoadSummariesAsync(token).ConfigureAwait(true))
                .Agents.ToDictionary(agent => agent.Id, agent => agent.Name, StringComparer.Ordinal);
            var items = titleSessions
                .Concat(contentSessions)
                .DistinctBy(session => session.Id, StringComparer.Ordinal)
                .Take(MaxResults)
                .Select(session => ToSearchItem(session, agentNames))
                .ToList();

            if (token.IsCancellationRequested || !IsOpen) return;
            // The view model re-checks the query against its own SearchText and drops a stale one.
            Vm.ApplySearchResults(query, items, matches);
            SelectFirstResult();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
    }

    private static SessionSearchItem ToSearchItem(
        ConnectOnion.WinUIClient.Models.SessionSummary session,
        IReadOnlyDictionary<string, string> agentNames)
        => new()
        {
            SessionId = session.Id,
            AgentId = session.AgentId,
            Title = session.Title,
            AgentName = agentNames.TryGetValue(session.AgentId, out var name) ? name : "",
            UpdatedAt = session.UpdatedAt,
        };

    private void SelectFirstResult()
        => ResultsList.SelectedIndex = Vm.Results.Count > 0 ? 0 : -1;

    private void MoveSelection(int delta)
    {
        if (Vm.Results.Count == 0) return;

        var current = ResultsList.SelectedIndex;
        var next = current < 0
            ? (delta > 0 ? 0 : Vm.Results.Count - 1)
            : Math.Clamp(current + delta, 0, Vm.Results.Count - 1);
        ResultsList.SelectedIndex = next;
        ResultsList.ScrollIntoView(Vm.Results[next]);
    }

    private void ActivateSelectedResult()
    {
        var selected = ResultsList.SelectedItem as SessionSearchItem
            ?? Vm.Results.FirstOrDefault();
        if (selected is null) return;

        SessionSelected?.Invoke(
            this,
            new SessionSearchSelectionEventArgs(selected.AgentId, selected.SessionId));
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                ActivateSelectedResult();
                e.Handled = true;
                break;
        }
    }

    private void ResultsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        ActivateSelectedResult();
        e.Handled = true;
    }

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SessionSearchItem item) return;
        SessionSelected?.Invoke(this, new SessionSearchSelectionEventArgs(item.AgentId, item.SessionId));
    }

    private void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Backdrop_Tapped(object sender, TappedRoutedEventArgs e) => RequestClose();

    private void ModalContainer_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void OverlayRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        RequestClose();
    }
}

public sealed class SessionSearchSelectionEventArgs(string agentId, string sessionId) : EventArgs
{
    public string AgentId { get; } = agentId;
    public string SessionId { get; } = sessionId;
}
