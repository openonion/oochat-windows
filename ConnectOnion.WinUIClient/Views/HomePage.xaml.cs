using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Views;

/// <summary>A compact, view-only projection of one saved agent.</summary>
public sealed partial class HomeAgentItem : Common.ObservableObject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName => FriendlyAgentName.From(Name);
    public string Initial { get; set; } = "?";
    public string ShortAddress { get; set; } = "";
    public string FullAddress { get; set; } = "";
    public string ConnectionSummary { get; set; } = "";
    public string Address { get; set; } = "";
    public string? DirectUrl { get; set; }
    public string LastUsedLabel { get; set; } = "Not used yet";
    public string? IconPath { get; set; }
    public bool IsSelected { get; set; }
    public bool ShowSelection => IsSelected || IsHighlighted;
    public string AutomationName
        => $"Open {DisplayName}. {ConnectionSummary}. {StatusLabel}. Last used {LastUsedLabel}.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(StatusAutomationName))]
    [NotifyPropertyChangedFor(nameof(IsStandardStatus))]
    public partial AgentPresence Presence { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(StatusAutomationName))]
    [NotifyPropertyChangedFor(nameof(IsStandardStatus))]
    public partial bool IsUnavailable { get; set; }

    [ObservableProperty]
    public partial bool IsOpening { get; set; }

    [ObservableProperty]
    public partial bool IsInteractionEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelection))]
    public partial bool IsHighlighted { get; set; }

    // Row density, pushed down by HomePage.ApplyRowDensity when the page is measured.
    //
    // These used to be an AdaptiveTrigger VisualStateGroup inside the row's DataTemplate, which
    // measured the *window* — but the content pane is up to a whole sidebar narrower than the
    // window, so a 920px window put the "detailed" columns into a ~630px row. The page-level
    // states above already avoid that trap (see WideViewportMinWidth); the row states could not,
    // because a VisualStateManager inside a DataTemplate has no way to see its own viewport.
    [ObservableProperty]
    public partial bool ShowStatusLabel { get; set; }

    [ObservableProperty]
    public partial bool ShowLastUsed { get; set; }

    [ObservableProperty]
    public partial bool ShowEndpoint { get; set; }

    public HomeAgentItem()
    {
        // The status word is the one that survives at every width; the other two are extras that
        // only appear once the row is genuinely wide. Seeded here because a partial-property
        // declaration cannot carry an initializer.
        ShowStatusLabel = true;
    }

    public bool IsChecking => Presence is AgentPresence.Unknown or AgentPresence.Checking;
    public bool IsStandardStatus => !IsChecking && !IsUnavailable;

    public string StatusLabel => IsUnavailable ? "Unavailable" : Presence switch
    {
        AgentPresence.Online => "Online",
        AgentPresence.Offline => "Offline",
        _ => "Checking",
    };

    public string StatusAutomationName => $"{DisplayName}: {StatusLabel}";
}

/// <summary>
/// Agent picker. The virtualized list owns vertical scrolling; add-agent is a transient dialog.
/// </summary>
public sealed partial class HomePage : Page, IReloadablePage, IShutdownDisarmable, IDisposable
{
    private const int MaxEagerPresenceProbes = 32;
    // The wide content is 1120px plus its 48px margins on both sides. This threshold must use the
    // page viewport, not AdaptiveTrigger's top-level window width: during startup the shell can
    // restore from a wider presenter before the content Frame receives its final size, and in
    // steady state the sidebar takes up to 288px that the window width would wrongly count as
    // available. The row-level thresholds below are on the page for the same reason.
    private const double WideContentWidth = 1120;
    private const double WideContentSideMargin = 48;
    private const double WideViewportMinWidth = WideContentWidth + (2 * WideContentSideMargin);
    private const double RegularViewportMinWidth = 600;

    private readonly AgentPresenceService _presence = AppServices.Presence;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _isOpeningAgent;
    private int _disposed;

    public ObservableCollection<HomeAgentItem> Agents { get; } = new();
    public bool HasAgents => Agents.Count > 0;

    public HomePage()
    {
        InitializeComponent();
        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;
    }

    /// <summary>Row is wide enough for the "Last used" column and the endpoint line.</summary>
    private const double DetailedRowMinWidth = 920;

    /// <summary>Row is wide enough to spell the status out rather than show only its dot.</summary>
    private const double LabelledStatusRowMinWidth = 560;

    private void PageRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        VisualStateManager.GoToState(
            this,
            e.NewSize.Width >= WideViewportMinWidth
                ? "Wide"
                : e.NewSize.Width >= RegularViewportMinWidth
                    ? "Adaptive"
                    : "Narrow",
            useTransitions: false);

        ApplyRowDensity(e.NewSize.Width);
    }

    /// <summary>
    /// Pushes the current viewport width down into every row. The rows used to decide this for
    /// themselves with <c>AdaptiveTrigger MinWindowWidth</c>, which reads the top-level window and
    /// therefore counted the sidebar's width as if it were available to the row.
    /// </summary>
    private void ApplyRowDensity(double viewportWidth)
    {
        var detailed = viewportWidth >= DetailedRowMinWidth;
        var labelledStatus = viewportWidth >= LabelledStatusRowMinWidth;

        foreach (var item in Agents)
        {
            item.ShowStatusLabel = labelledStatus;
            item.ShowLastUsed = detailed;
            item.ShowEndpoint = detailed;
        }
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        Dispose();
        base.OnNavigatedFrom(e);
    }

    public async Task ReloadAsync()
    {
        if (_disposed != 0) return;
        try { await LoadAsync(_lifetimeCts.Token); }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { return; }
    }

    /// <summary>
    /// Stops callbacks without mutating the bound collection. Window teardown does not reliably
    /// raise <see cref="FrameworkElement.Unloaded"/>, and an already queued presence callback can
    /// otherwise update this page after WinUI has begun destroying its native peer.
    /// </summary>
    public void DisarmForShutdown() => DisposeCore(clearBoundItems: false);

    public void Dispose() => DisposeCore(clearBoundItems: true);

    private void DisposeCore(bool clearBoundItems)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _presence.PresenceChanged -= OnPresenceChanged;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        Loaded -= HomePage_Loaded;
        Unloaded -= HomePage_Unloaded;

        // Clearing is useful when navigating away because it releases the item graph promptly.
        // During final window teardown it would itself invalidate an ItemsRepeater/ListView whose
        // native tree is already being dismantled, so DisarmForShutdown deliberately skips it.
        if (clearBoundItems) Agents.Clear();
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        _presence.PresenceChanged -= OnPresenceChanged;
        _presence.PresenceChanged += OnPresenceChanged;
        try { await LoadAsync(_lifetimeCts.Token); }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { return; }
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _presence.PresenceChanged -= OnPresenceChanged;
        Dispose();
    }

    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var state = await AppServices.Agents.LoadAsync(cancellationToken);
        var sessionsState = await AppServices.Sessions.LoadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Agents.Clear();
        var latestSessionByAgent = sessionsState.Sessions
            .GroupBy(session => session.AgentId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.MaxBy(session => session.UpdatedAt, StringComparer.Ordinal),
                StringComparer.Ordinal);

        for (var index = 0; index < state.Agents.Count; index++)
        {
            var agent = state.Agents[index];
            var item = new HomeAgentItem
            {
                Id = agent.Id,
                Name = agent.Name,
                Initial = NameInitial.FromPair(agent.DisplayName),
                ShortAddress = ShortenAddress(agent),
                FullAddress = GetAddress(agent),
                ConnectionSummary = BuildConnectionSummary(agent),
                Address = agent.Address,
                DirectUrl = agent.DirectUrl,
                LastUsedLabel = FormatLastUsed(
                    latestSessionByAgent.TryGetValue(agent.Id, out var latest) ? latest?.UpdatedAt : null),
                IconPath = agent.IconPath,
                IsInteractionEnabled = true,
                IsSelected = agent.Id == state.SelectedAgentId,
            };
            ApplyPresence(item);
            Agents.Add(item);

            if (index < MaxEagerPresenceProbes) _ = _presence.EnsureCheckedAsync(agent);
        }

        // Rows created after the last SizeChanged would otherwise sit at their constructed
        // density until the window is next resized.
        ApplyRowDensity(PageRoot.ActualWidth);

        Bindings.Update();
    }

    private void AgentList_ElementPrepared(
        ItemsRepeater sender,
        ItemsRepeaterElementPreparedEventArgs args)
    {
        if (sender.ItemsSourceView?.GetAt(args.Index) is not HomeAgentItem item) return;
        _ = _presence.EnsureCheckedAsync(item.Id, item.Address, item.DirectUrl);
    }

    private void OnPresenceChanged(string agentId)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            // Unsubscribing cannot retract a callback that was queued just before shutdown.
            if (Volatile.Read(ref _disposed) != 0) return;
            var item = Agents.FirstOrDefault(agent => agent.Id == agentId);
            if (item is not null) ApplyPresence(item);
        });
    }

    private void ApplyPresence(HomeAgentItem item)
    {
        var presence = _presence.GetPresence(item.Id);
        item.Presence = presence;
        item.IsUnavailable = presence == AgentPresence.Offline &&
            !_presence.GetDetail(item.Id).Contains("currently offline", StringComparison.OrdinalIgnoreCase);
    }

    private async void AgentCard_Click(object sender, RoutedEventArgs e)
    {
        if (_isOpeningAgent || (sender as FrameworkElement)?.Tag is not string agentId) return;

        _isOpeningAgent = true;
        PageErrorBar.IsOpen = false;
        var selected = Agents.FirstOrDefault(agent => agent.Id == agentId);
        foreach (var item in Agents) item.IsInteractionEnabled = false;
        if (selected is not null) selected.IsOpening = true;

        try
        {
            var agentsState = await AppServices.Agents.LoadAsync(_lifetimeCts.Token);
            if (agentsState.Agents.All(agent => agent.Id != agentId))
                throw new InvalidOperationException("This agent is no longer available.");

            await AppServices.Agents.SetSelectedAgentAsync(agentId, _lifetimeCts.Token);

            // Purely "this agent's most recent" — the call this replaced passed
            // activeSessionId: null, so the active pointer was deliberately not consulted. That is
            // LoadAgentSessionsAsync's exact semantics, and it reads one row instead of the index.
            var recent = await AppServices.Sessions.LoadAgentSessionsAsync(
                agentId, limit: 1, cancellationToken: _lifetimeCts.Token);
            var recentSession = recent.Sessions.Count > 0 ? recent.Sessions[0] : null;

            if (recentSession is null)
            {
                MainWindow.FromXamlRoot(XamlRoot)?.NavigateTo(typeof(AgentDetailPage));
                return;
            }

            await AppServices.Sessions.SetActiveSessionAsync(recentSession.Id, _lifetimeCts.Token);
            MainWindow.FromXamlRoot(XamlRoot)?.NavigateTo(typeof(ChatPage));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            PageErrorBar.Message = ex.Message;
            PageErrorBar.IsOpen = true;
        }
        finally
        {
            _isOpeningAgent = false;
            if (_disposed == 0)
            {
                foreach (var item in Agents) item.IsInteractionEnabled = true;
                if (selected is not null) selected.IsOpening = false;
            }
        }
    }

    private void AddAnotherAgent_Click(object sender, RoutedEventArgs e)
        => MainWindow.FromXamlRoot(XamlRoot)?.ShowAddAgentOverlay(sender as FrameworkElement);

    /// <summary>
    /// The empty state's "How do I get one?" link. Routed through the same
    /// <see cref="AppServices.UriLauncher"/> the Help menu uses, and reports failure rather than
    /// doing nothing — a user with no agent has nowhere else to go if this link is silent.
    /// </summary>
    private async void EmptyStateDocs_Click(object sender, RoutedEventArgs e)
    {
        var launched = await AppServices.UriLauncher.LaunchAsync(new Uri(MainWindow.DocsUrl));
        if (launched || Volatile.Read(ref _disposed) != 0) return;

        PageErrorBar.Title = LocalizedStrings.Get("HomeDocsOpenFailedTitle", "Couldn't open the docs");
        PageErrorBar.Message = LocalizedStrings.Format(
            "HomeDocsOpenFailedBody",
            "Visit {0} in your browser.",
            MainWindow.DocsUrl);
        PageErrorBar.IsOpen = true;
    }

    public async Task RefreshAfterAgentAddedAsync(string agentId)
    {
        if (_disposed != 0) return;
        try { await LoadAsync(_lifetimeCts.Token); }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { return; }

        var item = Agents.FirstOrDefault(agent => agent.Id == agentId);
        if (item is null) return;
        item.IsHighlighted = true;
        try { await Task.Delay(TimeSpan.FromSeconds(1.6), _lifetimeCts.Token); }
        finally { if (!_lifetimeCts.IsCancellationRequested) item.IsHighlighted = false; }
    }

    private static string ShortenAddress(AgentConfig agent)
    {
        var value = GetAddress(agent);
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            var authority = uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port}";
            var path = uri.AbsolutePath.TrimEnd('/');
            return string.IsNullOrEmpty(path) ? authority : $"{authority}{path}";
        }

        if (value.Length <= 20) return value;
        return $"{value[..10]}…{value[^6..]}";
    }

    private static string GetAddress(AgentConfig agent)
        => string.IsNullOrWhiteSpace(agent.Address) ? agent.DirectUrl ?? "" : agent.Address;

    private static string BuildConnectionSummary(AgentConfig agent)
    {
        var transport = string.IsNullOrWhiteSpace(agent.DirectUrl)
            ? "Relay connection"
            : "Direct connection";
        var model = ReadInfoString(agent.InfoJson, "model");
        return string.IsNullOrWhiteSpace(model) ? transport : $"{model} · {transport}";
    }

    private static string? ReadInfoString(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
        }
        catch (JsonException)
        {
            // Cached metadata is optional presentation data; an old or partial payload simply
            // falls back to the transport label.
        }

        return null;
    }

    private static string FormatLastUsed(string? timestamp)
    {
        if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, out var parsed))
            return "Not used yet";

        var local = parsed.ToLocalTime();
        var elapsed = DateTimeOffset.Now - local;
        if (elapsed < TimeSpan.Zero || elapsed < TimeSpan.FromMinutes(1)) return "Just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} min ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)elapsed.TotalHours)} hr ago";
        if (local.Date == DateTimeOffset.Now.Date.AddDays(-1)) return "Yesterday";
        if (elapsed < TimeSpan.FromDays(7))
            return local.ToString("dddd", CultureInfo.CurrentCulture);
        return local.ToString("MMM d", CultureInfo.CurrentCulture);
    }

}
