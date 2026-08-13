using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Dispatching;
using Windows.UI.ViewManagement;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// The agent/session tree down the left of the shell, plus the pinned-conversation section and
/// the online count.
///
/// Unlike the app's other surfaces this control has no view model — it owns its collections and
/// reaches storage through <c>AppServices</c> directly, because WinUI constructs it from XAML.
/// It also raises navigation as an event rather than performing it: the sidebar knows *what*
/// the user picked, and <c>MainWindow</c> owns the frame that acts on it.
///
/// A <see cref="RefreshAsync"/> that finds the tree's <i>shape or content</i> changed rebuilds it
/// wholesale from storage — there is no incremental splice. That keeps it simple and correct at the
/// cost of losing transient UI state, which is exactly why expansion state is remembered separately
/// (see <see cref="_expandedAgentIds"/>).
///
/// <para>Selection is the one exception, and it has to be: clicking a conversation moves the
/// active-session and selected-agent pointers, nothing else, and it is by far the most frequent
/// thing the user does. That is compared separately (<see cref="ComputeSelectionSignature"/>) and
/// applied in place (<see cref="ApplySelection"/>), so switching conversations never discards a
/// single row. Adding new state to a row means deciding which of the two signatures owns it:
/// anything that changes which rows exist or what they say belongs in the structure signature.</para>
/// </summary>
public sealed partial class ShellSidebar : UserControl, INotifyPropertyChanged
{
    /// <summary>The agents and their conversations, as a tree. This stays the model the rest of
    /// the class works against — <see cref="FindShellAgent"/>, the presence updates, the selection
    /// repaint and <see cref="RememberExpandedAgentsFromUi"/> all read it. It is no longer what
    /// XAML binds to; <see cref="SidebarRows"/> is.</summary>
    public ObservableCollection<ShellAgentItem> ShellAgents { get; } = new();
    public ObservableCollection<ShellSessionItem> PinnedSessions { get; } = new();

    /// <summary>
    /// The tree above, flattened into the one list the sidebar's single <c>ItemsRepeater</c>
    /// renders — pinned header, pinned conversations, then each agent followed (when expanded) by
    /// its time-group labels and conversations.
    ///
    /// <para>Flat because nested <c>ItemsRepeater</c>s do not virtualize: an inner repeater is
    /// measured with an unconstrained height by its parent's item container, so it realizes every
    /// item it has. See <c>ShellSidebarRows.cs</c>. The consequence for this class is that
    /// expansion is a list rebuild rather than a <c>Visibility</c> flip — a collapsed agent's rows
    /// are absent, not hidden — which is what <see cref="RebuildSidebarRows"/> exists for.</para>
    ///
    /// <para><c>object</c> rather than a common base type: the rows have nothing in common
    /// behaviourally, and giving them a shared base purely to name this collection would have
    /// forced <c>ShellAgentItem</c> and <c>ShellSessionItem</c> to change their own hierarchy.
    /// <c>ShellSidebarRowTemplateSelector</c> dispatches on the concrete type.</para>
    /// </summary>
    public ObservableRangeCollection<object> SidebarRows { get; } = new();

    /// <summary>One instance, reused across rebuilds, so the pinned section's chevron keeps its
    /// binding rather than being re-created (and re-bound) every time the tree is rebuilt.</summary>
    private readonly ShellPinnedHeaderRow _pinnedHeaderRow = new();

    /// <summary>How many conversations one "page" of an agent's branch holds. Sized to comfortably
    /// exceed a tall window's worth of rows, so the common user never sees a "show more" at all
    /// and the paging is invisible until a branch is genuinely long.</summary>
    private const int SessionPageSize = 25;

    /// <summary>
    /// How many agents a refresh probes up front, before the rest are left to
    /// <see cref="AgentRow_ElementPrepared"/> to probe as they scroll into view.
    ///
    /// <para>The sweep used to be unconditional over every agent. Each probe is an HTTP call with
    /// a retry pipeline, and the offline TTL is 30 s — so a user with a few hundred agents was
    /// generating a few hundred requests every half minute for rows they could not see, throttled
    /// to four at a time and therefore never actually finishing before the next sweep was due.</para>
    ///
    /// <para>Set well above what anyone accumulates by hand (agents are added one at a time, each
    /// needing an address and an onboarding step), so for a real sidebar this is the old behaviour
    /// exactly: every agent probed, online count complete on first paint. It only bites at counts
    /// where the unbounded sweep was itself the problem. Deliberately not "only what is visible" —
    /// an expanded branch of 25 conversations pushes the other agents below the fold, and their
    /// status dots going blank because a conversation list is long would be a regression.</para>
    /// </summary>
    private const int MaxEagerPresenceProbes = 32;

    public event Action<Type, bool>? NavigationRequested;
    /// <summary>
    /// Requests a new top-level navigation root after destructive state changes. Unlike an
    /// ordinary navigation, the shell clears Back/Forward history so an entity-backed page that
    /// has just become invalid cannot be reopened with the title-bar arrows.
    /// </summary>
    public event Action<Type>? NavigationResetRequested;
    public event Action<FrameworkElement>? SettingsRequested;
    public event Action<FrameworkElement>? SessionSearchRequested;
    /// <summary>The sidebar click means "I want the
    /// add-agent form open", not merely "show me the landing page" — the shell answers it by
    /// opening the overlay without navigating. The button is passed back for focus restoration.</summary>
    public event Action<FrameworkElement>? AddAgentRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly AgentPresenceService _presence = AppServices.Presence;
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
    /// <summary>Which agents the user has expanded. Held here rather than on the items because
    /// <see cref="RefreshAsync"/> discards and rebuilds every item — without this, any refresh
    /// (a rename, a new conversation, a presence sweep) would collapse the whole tree.</summary>
    private readonly HashSet<string> _expandedAgentIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string UpdatedAt, string Preview)> _sessionPreviewCache =
        new(StringComparer.Ordinal);
    private Type? _currentPageType;
    private int _onlineAgentCount;
    /// <summary>False until the first refresh completes. Distinguishes "the user has collapsed
    /// every agent" from "we have not observed any expansion state yet" — the first refresh
    /// falls back to expanding the selected agent, later ones honour the empty set.</summary>
    private bool _hasExpansionState;
    private bool _loadedPersistedExpansionState;
    private bool _isPinnedExpanded = true;
    /// <summary>Hash of everything that decides which rows exist. Null until the first refresh has
    /// rendered, which is also what tells the guards below that there is a tree to compare
    /// against.</summary>
    private int? _lastStructureSignature;
    /// <summary>Hash of which row is lit, tracked apart from the structure so a selection change
    /// repaints in place instead of rebuilding. See <see cref="ComputeSelectionSignature"/>.</summary>
    private int _lastSelectionSignature;
    /// <summary>The <see cref="Data.StorageRevision"/> and page the last full load was made
    /// against, so an unchanged refresh can return without touching SQLite at all.</summary>
    private long _lastLoadedRevision = -1;
    private Type? _lastLoadedPageType;
    /// <summary>The agents the last load saw, kept only so the skip path above can still keep the
    /// presence probes running — those are TTL-based and must not stall just because the tree
    /// did not need redrawing.</summary>
    private IReadOnlyList<(string Id, string Address, string? DirectUrl)> _knownAgents =
        Array.Empty<(string, string, string?)>();
    /// <summary>
    /// Monotonically identifies refresh attempts. Storage reads yield, so an older attempt can
    /// otherwise resume after a newer navigation and repaint the previous selection. Only the
    /// newest attempt is allowed to commit cache markers or rebuild the row collections.
    /// </summary>
    private long _refreshGeneration;
    /// <summary>
    /// Serializes the two metadata writes behind a conversation click. The row highlight is
    /// applied before this queue, so rapid clicks remain responsive while persistence still
    /// finishes in click order and the last click is the only one allowed to navigate.
    /// </summary>
    private readonly object _sessionSelectionQueueGate = new();
    private System.Threading.Tasks.Task _sessionSelectionTail =
        System.Threading.Tasks.Task.CompletedTask;
    private long _sessionSelectionGeneration;
    private int _shutdown;

    public int OnlineAgentCount
    {
        get => _onlineAgentCount;
        private set
        {
            if (_onlineAgentCount == value) return;
            _onlineAgentCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OnlineAgentCountText));
            OnPropertyChanged(nameof(OnlineAgentStatusText));
            OnPropertyChanged(nameof(HasOnlineAgents));
        }
    }

    public string AppVersionBadgeText => $"v{AppVersionService.DisplayVersion}";
    public bool HasOnlineAgents => OnlineAgentCount > 0;
    public string OnlineAgentCountText => OnlineAgentCount switch
    {
        0 => LocalizedStrings.Get("AgentCountNoneOnline", "No agents online"),
        1 => LocalizedStrings.Get("AgentCountOneOnline", "1 agent online"),
        _ => LocalizedStrings.Format(
            "AgentCountOnline",
            "{0} agents online",
            OnlineAgentCount),
    };
    public string OnlineAgentStatusText => OnlineAgentCountText.ToUpper(CultureInfo.CurrentUICulture);
    public bool HasPinnedSessions => PinnedSessions.Count > 0;
    public double PinnedChevronAngle => IsPinnedExpanded ? 0 : 270;
    public string PinnedToggleAccessibilityName => IsPinnedExpanded
        ? LocalizedStrings.Get("SidebarCollapsePinnedChats", "Collapse pinned shortcuts")
        : LocalizedStrings.Get("SidebarExpandPinnedChats", "Expand pinned shortcuts");
    public bool IsPinnedExpanded
    {
        get => _isPinnedExpanded;
        private set
        {
            if (_isPinnedExpanded == value) return;
            _isPinnedExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PinnedChevronAngle));
            OnPropertyChanged(nameof(PinnedToggleAccessibilityName));
        }
    }

    public ShellSidebar()
    {
        InitializeComponent();
        DataContext = this;
        _presence.PresenceChanged += OnPresenceChanged;

        // The presence event is app-level and long-lived. Without detaching, a torn-down
        // sidebar (e.g. after a forceReload navigation) keeps receiving events while its
        // DispatcherQueue is null — the NullReferenceException in OnPresenceChanged. Detach
        // on unload; re-attach on (re)load in case the control is moved in the tree.
        Loaded += OnLoadedAttachPresence;
        Unloaded += OnUnloadedDetachPresence;
    }

    private void OnLoadedAttachPresence(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _shutdown) != 0) return;
        _presence.PresenceChanged -= OnPresenceChanged;
        _presence.PresenceChanged += OnPresenceChanged;
    }

    private void OnUnloadedDetachPresence(object sender, RoutedEventArgs e)
        => _presence.PresenceChanged -= OnPresenceChanged;

    /// <summary>
    /// Synchronously detaches every app-level callback before the window's native XAML tree is
    /// destroyed. Unloaded is not guaranteed on application exit, and removing the event alone
    /// cannot retract a presence update that is already queued on the dispatcher.
    /// </summary>
    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        _presence.PresenceChanged -= OnPresenceChanged;
        Loaded -= OnLoadedAttachPresence;
        Unloaded -= OnUnloadedDetachPresence;
    }

    public void SetCurrentPage(Type? pageType)
    {
        _currentPageType = pageType;
        // Agent library is a navigation destination, so it carries a selected state like any
        // other. This is the only thing that can change it — selection is derived purely from
        // which page the frame is on, never stored.
        OnPropertyChanged(nameof(IsAgentLibrarySelected));
    }

    /// <summary>
    /// Whether the shell is currently *on* the agent library, which is what the nav button paints
    /// its selected background and accent rail from.
    ///
    /// <para>Deliberately not symmetric with <see cref="IsAgentSelected"/>: that one asks "is this
    /// agent the chosen one", which is only meaningful on a page about an agent. This asks which
    /// destination the frame is showing, so exactly one nav row is ever lit.</para>
    /// </summary>
    public bool IsAgentLibrarySelected => _currentPageType == typeof(HomePage);

    public void FocusFirstNavigation()
        => AgentsNavigationButton.Focus(FocusState.Programmatic);

    /// <summary>Rebuilds the entire tree from storage. Called after anything that could change
    /// what the sidebar shows — agent added/renamed/deleted, conversation created/pinned/removed,
    /// navigation. Cheap enough to be unconditional at this scale (tens of rows).</summary>
    public async System.Threading.Tasks.Task RefreshAsync()
    {
        if (Volatile.Read(ref _shutdown) != 0) return;
        var refreshGeneration = Interlocked.Increment(ref _refreshGeneration);

        // Cheapest possible short-circuit, and it has to come first. The render-signature guard
        // further down avoids rebuilding the tree, but it is computed *from* the data — so a
        // navigation that changed nothing still cost a full agent-table read, a full session-table
        // read and a batch of preview queries before it could conclude there was nothing to do.
        // This method runs on every navigation, so that was the common case, not the rare one.
        //
        // The comparison also carries the current page type, because row selection is
        // page-dependent (see IsAgentSelected): moving between Home and an agent page changes what
        // the tree should render without changing a byte of storage.
        var revision = Data.StorageRevision.Current;
        if (_lastLoadedRevision == revision
            && _lastLoadedPageType == _currentPageType
            && _lastStructureSignature is not null)
        {
            ProbeAgentPresence();
            return;
        }

        // Summaries, not full records: this runs on every navigation and needs only what an
        // agent row draws and probes with. See AgentRepository.LoadSummariesAsync.
        var agentsState = await AppServices.Agents.LoadSummariesAsync();
        if (IsRefreshSuperseded(refreshGeneration)) return;

        // Must run *before* the Clear below, while the old items still hold their expansion — and
        // before the session loads, which now depend on which agents are expanded.
        RememberExpandedAgentsFromUi();
        var pagesByAgent = ShellAgents.ToDictionary(
            agent => agent.Id,
            agent => Math.Max(1, agent.LoadedPages),
            StringComparer.Ordinal);
        if (!_loadedPersistedExpansionState)
        {
            var persisted = await AppServices.SidebarState.LoadAsync();
            if (IsRefreshSuperseded(refreshGeneration)) return;
            if (!_hasExpansionState && persisted.HasAgentExpansionState)
            {
                _expandedAgentIds.Clear();
                _expandedAgentIds.UnionWith(persisted.ExpandedAgentIds);
                _hasExpansionState = true;
            }
            IsPinnedExpanded = persisted.IsPinnedExpanded;
            _loadedPersistedExpansionState = true;
        }
        if (IsRefreshSuperseded(refreshGeneration)) return;

        // Sessions are fetched per agent, and only for the ones that are open. A collapsed branch
        // costs nothing — that is the point of paging here, and it is where most of the saving is:
        // the whole index used to be read regardless of what the user could actually see. An
        // expanded branch reloads as many pages as it had, so an incoming message cannot collapse
        // a branch the user had paged several deep.
        var activeSessionId = await AppServices.Sessions.GetActiveSessionIdAsync();
        if (IsRefreshSuperseded(refreshGeneration)) return;
        var pinned = await AppServices.Sessions.LoadPinnedAsync();
        if (IsRefreshSuperseded(refreshGeneration)) return;

        // One aggregate over `sessions`, independent of which branches are open. It has to be:
        // the agent rows that need a rolled-up badge are the collapsed ones, whose conversations
        // are deliberately never fetched below. Returns only agents with something to report.
        var attentionByAgent = await AppServices.Sessions.GetAgentAttentionAsync();
        if (IsRefreshSuperseded(refreshGeneration)) return;

        var loadedByAgent = new Dictionary<string, SessionPage>(StringComparer.Ordinal);
        foreach (var agent in agentsState.Agents)
        {
            var expanded = _hasExpansionState
                ? _expandedAgentIds.Contains(agent.Id)
                : agent.Id == agentsState.SelectedAgentId;
            if (!expanded) continue;

            var pages = pagesByAgent.TryGetValue(agent.Id, out var previous) ? previous : 1;
            var page = await AppServices.Sessions.LoadAgentSessionsAsync(
                agent.Id, SessionPageSize * pages);
            if (IsRefreshSuperseded(refreshGeneration)) return;
            loadedByAgent[agent.Id] = await EnsureActiveSessionVisibleAsync(
                agent.Id, page, activeSessionId);
            if (IsRefreshSuperseded(refreshGeneration)) return;
        }

        await RefreshSessionPreviewCacheAsync(
            pinned.Concat(loadedByAgent.Values.SelectMany(page => page.Sessions)).ToList());
        if (IsRefreshSuperseded(refreshGeneration)) return;

        // Publish the cache markers only after the final await and supersession check. Setting
        // them earlier lets a newer refresh take the skip path while this older one is still
        // waiting, after which the older attempt is discarded and neither one repaints the rows.
        _lastLoadedRevision = revision;
        _lastLoadedPageType = _currentPageType;

        // Two signatures, not one, and the split is the whole point. Selecting a conversation
        // writes the active-session and selected-agent pointers, and both call
        // StorageRevision.Bump — so the cheap guard at the top of this method always misses on a
        // click. The old single signature folded the selection into it, so it missed too, and the
        // most frequent interaction in the app went all the way to Clear() + rebuild: every
        // ShellAgentItem and ShellSessionItem replaced, ReplaceAll raising a Reset, and the
        // repeater re-realizing every visible row. That threw away the in-place paint
        // SelectSessionAsync had already done a moment earlier, and it is what made an agent with
        // a custom icon visibly flash (see AgentAvatar's cache note).
        var structureSignature = ComputeStructureSignature(agentsState, pinned, loadedByAgent, attentionByAgent);
        var selectionSignature = ComputeSelectionSignature(agentsState.SelectedAgentId, activeSessionId);
        if (_lastStructureSignature == structureSignature)
        {
            // Same tree, possibly a different row lit. Repaint the flags on the items already on
            // screen; no collection is cleared, so nothing is re-realized.
            if (_lastSelectionSignature != selectionSignature)
            {
                ApplySelection(agentsState.SelectedAgentId, activeSessionId);
                _lastSelectionSignature = selectionSignature;
            }
            ProbeAgentPresence();
            return;
        }
        _lastStructureSignature = structureSignature;
        _lastSelectionSignature = selectionSignature;
        _knownAgents = agentsState.Agents
            .Select(agent => (agent.Id, agent.Address, agent.DirectUrl))
            .ToList();

        PinnedSessions.Clear();
        ShellAgents.Clear();
        // Already ordered newest-first by the query, and never paged — the pinned set is bounded
        // by how many the user chose to pin.
        foreach (var session in pinned)
        {
            var pinnedRow = CreateShellSessionItem(session, activeSessionId);
            // Marks this as the pinned-section copy so the row selector picks the pinned template
            // (different indent, PinnedSession_Click, PinnedSessionButton). The same conversation
            // also gets an ordinary row under its agent below, from a separate item.
            pinnedRow.IsPinnedRow = true;
            PinnedSessions.Add(pinnedRow);
        }

        foreach (var agent in agentsState.Agents)
        {
            attentionByAgent.TryGetValue(agent.Id, out var attention);
            var item = new ShellAgentItem
            {
                Id = agent.Id,
                Name = agent.Name,
                Address = agent.Address,
                DirectUrl = agent.DirectUrl,
                IconPath = agent.IconPath,
                UnreadCount = attention.UnreadCount,
                RequiresAttention = attention.RequiresAttention,
                IsSelected = IsAgentSelected(agent.Id, agentsState.SelectedAgentId),
                // First build: open the selected agent so the app doesn't start fully collapsed.
                // Afterwards the user's own expansion state is authoritative, including "all
                // collapsed" — which is why the flag exists rather than testing for an empty set.
                IsExpanded = _hasExpansionState
                    ? _expandedAgentIds.Contains(agent.Id)
                    : agent.Id == agentsState.SelectedAgentId,
                Presence = _presence.GetPresence(agent.Id),
            };

            // Already newest-first from SQL, so no sort here — the ORDER BY matches what the
            // ordinal UpdatedAt comparison used to do in memory.
            if (loadedByAgent.TryGetValue(agent.Id, out var page))
            {
                foreach (var session in page.Sessions)
                {
                    item.Sessions.Add(CreateShellSessionItem(session, activeSessionId));
                }
                item.HasMoreSessions = page.HasMore;
                item.NextSessionCursor = page.NextCursor;
                item.SessionsLoaded = true;
                item.LoadedPages = pagesByAgent.TryGetValue(agent.Id, out var pages) ? pages : 1;
            }
            item.RebuildSessionGroups();

            ShellAgents.Add(item);
        }

        OnPropertyChanged(nameof(HasPinnedSessions));
        UpdateOnlineAgentCount();
        // Fire-and-forget presence probes: the rows are already on screen with whatever the
        // service last knew, and each probe raises PresenceChanged to update its own dot when it
        // lands. Awaiting them would hold the whole sidebar behind the slowest network call.
        // Bounded — see MaxEagerPresenceProbes; the tail is probed by AgentRow_ElementPrepared as
        // it is scrolled into view.
        ProbeAgentPresence();

        _hasExpansionState = true;
        RebuildSidebarRows();
    }

    /// <summary>
    /// Projects <see cref="ShellAgents"/>/<see cref="PinnedSessions"/> and the current expansion
    /// state into the flat <see cref="SidebarRows"/> list the repeater renders.
    ///
    /// <para>Called after a refresh rebuilds the tree, and by the two disclosure handlers — with a
    /// flat list, expanding an agent means its conversation rows appear in the list rather than
    /// becoming visible. Rebuilding the whole list rather than splicing one agent's range is
    /// deliberate: the list is a few hundred small objects at worst, <c>ReplaceAll</c> raises a
    /// single Reset, and only the rows in the viewport are realized from it — so the cost is
    /// bounded by the window, not by the tree. Splicing would buy nothing and would need the
    /// agent's row range tracked and kept correct across every other mutation.</para>
    /// </summary>
    private void RebuildSidebarRows()
    {
        var rows = new List<object>(ShellAgents.Count + PinnedSessions.Count + 1);

        if (PinnedSessions.Count > 0)
        {
            _pinnedHeaderRow.IsExpanded = IsPinnedExpanded;
            rows.Add(_pinnedHeaderRow);
            if (IsPinnedExpanded) rows.AddRange(PinnedSessions);
        }

        foreach (var agent in ShellAgents)
        {
            rows.Add(agent);
            if (!agent.IsExpanded) continue;

            // The grouping still decides order and where the labels fall; only the label's
            // container changed, from a nested list's header to a row of its own.
            foreach (var group in agent.SessionGroups)
            {
                rows.Add(new ShellSessionGroupHeaderRow(group.Label));
                rows.AddRange(group.Sessions);
            }

            if (agent.HasMoreSessions) rows.Add(new ShellShowMoreSessionsRow(agent.Id));
        }

        SidebarRows.ReplaceAll(rows);
    }

    /// <summary>
    /// Guarantees the conversation currently on screen is in the page its agent renders.
    ///
    /// <para>Without this, opening a conversation far down a long branch would draw the branch with
    /// no row selected — the selected row simply is not in the first page, so the sidebar looks
    /// like it lost track of where the user is. Fetched as one extra row by id and inserted in
    /// sorted position, and only when it is genuinely missing, so the usual case pays nothing.</para>
    ///
    /// <para><see cref="SessionPage.HasMore"/> is carried through untouched: the older
    /// conversations that were skipped over still exist, so the branch must still offer to load
    /// them.</para>
    /// </summary>
    private static async System.Threading.Tasks.Task<SessionPage> EnsureActiveSessionVisibleAsync(
        string agentId,
        SessionPage page,
        string? activeSessionId)
    {
        if (activeSessionId is null) return page;
        if (page.Sessions.Any(session => session.Id == activeSessionId)) return page;

        var active = await AppServices.Sessions.GetSessionAsync(activeSessionId);
        if (active is null || active.AgentId != agentId) return page;

        var sessions = page.Sessions.ToList();
        // Same ordering the query uses, so the row lands where it would have been.
        var index = sessions.FindIndex(session =>
            string.CompareOrdinal(session.UpdatedAt, active.UpdatedAt) < 0);
        if (index < 0) sessions.Add(active);
        else sessions.Insert(index, active);

        return new SessionPage(sessions, page.HasMore);
    }

    /// <summary>
    /// Fetches an agent's first page, for the branch the user has just opened for the first time.
    /// Separate from <see cref="LoadMoreSessionsAsync"/> because there is no cursor yet and because
    /// this one replaces the branch's contents rather than appending to them.
    /// </summary>
    private async System.Threading.Tasks.Task LoadFirstPageAsync(ShellAgentItem agent)
    {
        var page = await AppServices.Sessions.LoadAgentSessionsAsync(agent.Id, SessionPageSize);
        if (Volatile.Read(ref _shutdown) != 0) return;

        var activeSessionId = await AppServices.Sessions.GetActiveSessionIdAsync();
        if (Volatile.Read(ref _shutdown) != 0) return;
        page = await EnsureActiveSessionVisibleAsync(agent.Id, page, activeSessionId);
        if (Volatile.Read(ref _shutdown) != 0) return;

        await RefreshSessionPreviewCacheAsync(page.Sessions, prune: false);
        if (Volatile.Read(ref _shutdown) != 0) return;

        // The user can have collapsed the branch again while this was in flight; the rows are still
        // recorded so a re-expansion is instant, they just are not rendered.
        agent.Sessions.Clear();
        foreach (var session in page.Sessions)
        {
            agent.Sessions.Add(CreateShellSessionItem(session, activeSessionId));
        }
        agent.HasMoreSessions = page.HasMore;
        agent.NextSessionCursor = page.NextCursor;
        agent.LoadedPages = 1;
        agent.SessionsLoaded = true;
        agent.RebuildSessionGroups();

        _lastStructureSignature = null;
        RebuildSidebarRows();
    }

    /// <summary>Pulls the next page into one agent's branch. The cursor is the branch's oldest
    /// loaded conversation, so this continues where the last page stopped rather than re-reading
    /// from the top.</summary>
    private async System.Threading.Tasks.Task LoadMoreSessionsAsync(string agentId)
    {
        var agent = FindShellAgent(agentId);
        if (agent is null || !agent.HasMoreSessions) return;
        if (agent.NextSessionCursor is not { } cursor) return;

        var next = await AppServices.Sessions.LoadAgentSessionsAsync(
            agentId, SessionPageSize, after: cursor);
        if (Volatile.Read(ref _shutdown) != 0) return;

        var activeSessionId = await AppServices.Sessions.GetActiveSessionIdAsync();
        if (Volatile.Read(ref _shutdown) != 0) return;

        await RefreshSessionPreviewCacheAsync(next.Sessions, prune: false);
        if (Volatile.Read(ref _shutdown) != 0) return;

        foreach (var session in next.Sessions)
        {
            agent.Sessions.Add(CreateShellSessionItem(session, activeSessionId));
        }
        agent.HasMoreSessions = next.HasMore;
        agent.NextSessionCursor = next.NextCursor;
        agent.LoadedPages++;
        agent.RebuildSessionGroups();

        // The signature is computed from what was loaded, so it has to move with a page that just
        // arrived — otherwise the next refresh would decide nothing had changed and skip the
        // rebuild that puts these rows on screen.
        _lastStructureSignature = null;
        RebuildSidebarRows();
    }

    private bool IsRefreshSuperseded(long refreshGeneration)
        => Volatile.Read(ref _shutdown) != 0
           || refreshGeneration != Interlocked.Read(ref _refreshGeneration);

    /// <summary>Keeps the reachability probes ticking on the path that skips the reload. They are
    /// TTL-based and coalesced inside the service, so calling this on every navigation costs a
    /// dictionary lookup per agent when nothing is due.</summary>
    private void ProbeAgentPresence()
    {
        if (Volatile.Read(ref _shutdown) != 0) return;
        foreach (var (id, address, directUrl) in _knownAgents.Take(MaxEagerPresenceProbes))
        {
            _ = _presence.EnsureCheckedAsync(id, address, directUrl);
        }
    }

    /// <summary>
    /// Probes an agent whose row has just been realized by the repeater.
    ///
    /// <para>This is what keeps presence bounded past <see cref="MaxEagerPresenceProbes"/>: beyond
    /// that, an agent is checked when it is actually put on screen rather than because it exists.
    /// The service coalesces and TTL-caches, so a row that scrolls in and out repeatedly costs a
    /// dictionary lookup, not a request.</para>
    /// </summary>
    private void AgentRow_ElementPrepared(
        Microsoft.UI.Xaml.Controls.ItemsRepeater sender,
        Microsoft.UI.Xaml.Controls.ItemsRepeaterElementPreparedEventArgs args)
    {
        if (Volatile.Read(ref _shutdown) != 0) return;
        if (sender.ItemsSourceView?.GetAt(args.Index) is not ShellAgentItem agent) return;

        _ = _presence.EnsureCheckedAsync(agent.Id, agent.Address, agent.DirectUrl);
    }

    /// <summary>
    /// Which row is lit. Page type belongs here rather than in the structure signature because
    /// selection is page-dependent, not purely storage-derived: <see cref="IsAgentSelected"/> and
    /// <see cref="IsSessionSelected"/> both gate on <c>_currentPageType</c>, so the same selected
    /// agent and active conversation must render differently on <c>AgentDetailPage</c> than on
    /// <c>ChatPage</c>. Leaving it out of the comparison entirely made the common "open an agent,
    /// then click the conversation you were already in" path skip the repaint — storage genuinely
    /// had not changed — so the tree kept the agent row lit and the conversation row unselected.
    /// </summary>
    private int ComputeSelectionSignature(string? selectedAgentId, string? activeSessionId)
    {
        var hash = new HashCode();
        hash.Add(_currentPageType);
        hash.Add(selectedAgentId, StringComparer.Ordinal);
        hash.Add(activeSessionId, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Everything that decides which rows exist and what they say — deliberately excluding the
    /// selection, which <see cref="ComputeSelectionSignature"/> owns and which can be repainted
    /// without rebuilding anything.
    ///
    /// <para><c>SelectedAgentId</c> is safe to leave out even though it seeds the initial
    /// expansion (<c>IsExpanded = … : agent.Id == SelectedAgentId</c>): that branch only runs
    /// while <c>_hasExpansionState</c> is false, which is only true before the first completed
    /// refresh — and until that refresh completes <c>_lastStructureSignature</c> is null, so the
    /// guard this feeds cannot pass. Afterwards expansion comes from
    /// <c>_expandedAgentIds</c>, which is hashed below.</para>
    /// </summary>
    private int ComputeStructureSignature(
        AgentSummaryState agentsState,
        IReadOnlyList<SessionSummary> pinned,
        IReadOnlyDictionary<string, SessionPage> loadedByAgent,
        IReadOnlyDictionary<string, AgentAttention> attentionByAgent)
    {
        var hash = new HashCode();
        hash.Add(IsPinnedExpanded);
        foreach (var expanded in _expandedAgentIds.OrderBy(id => id, StringComparer.Ordinal))
            hash.Add(expanded, StringComparer.Ordinal);
        foreach (var agent in agentsState.Agents)
        {
            hash.Add(agent.Id, StringComparer.Ordinal);
            hash.Add(agent.Name, StringComparer.Ordinal);
            hash.Add(agent.Address, StringComparer.Ordinal);
            hash.Add(agent.DirectUrl, StringComparer.Ordinal);
            hash.Add(agent.IconPath, StringComparer.Ordinal);
            // The rolled-up badge on a collapsed branch, whose conversation rows are not in this
            // signature (or in the tree) at all — so without this, a reply arriving for a collapsed
            // agent changes nothing the signature can see and the badge never appears.
            attentionByAgent.TryGetValue(agent.Id, out var attention);
            hash.Add(attention.UnreadCount);
            hash.Add(attention.RequiresAttention);
        }
        AddSessions(pinned);
        // Iterated in agent order rather than dictionary order: a HashCode is order-sensitive, and
        // a Dictionary makes no ordering promise, so hashing its values directly would let the
        // signature change without the tree having changed.
        foreach (var agent in agentsState.Agents)
        {
            if (!loadedByAgent.TryGetValue(agent.Id, out var page)) continue;
            AddSessions(page.Sessions);
            // Part of what is rendered — it decides whether the branch ends with a "show more" row.
            hash.Add(page.HasMore);
        }
        return hash.ToHashCode();

        void AddSessions(IReadOnlyList<SessionSummary> sessions)
        {
            foreach (var session in sessions)
            {
                hash.Add(session.Id, StringComparer.Ordinal);
                hash.Add(session.AgentId, StringComparer.Ordinal);
                hash.Add(session.Title, StringComparer.Ordinal);
                hash.Add(session.UpdatedAt, StringComparer.Ordinal);
                hash.Add(session.IsPinned);
                // Both values paint visible badges. Clearing them does not change UpdatedAt, so
                // omitting them makes an opened conversation keep its stale unread/attention UI.
                hash.Add(session.UnreadCount);
                hash.Add(session.RequiresAttention);
                if (_sessionPreviewCache.TryGetValue(session.Id, out var preview))
                    hash.Add(preview.Preview, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Ctrl+N / "New chat". Returns false when there is no agent to chat with, which
    /// lets the window surface the add-agent requirement.</summary>
    public async System.Threading.Tasks.Task<bool> StartNewChatAsync()
    {
        var agentsState = await AppServices.Agents.LoadAsync();
        // Prefer the selected agent, else any agent: a new-chat shortcut pressed with nothing
        // selected should still do something sensible rather than silently no-op.
        var agent = agentsState.Agents.FirstOrDefault(entry => entry.Id == agentsState.SelectedAgentId)
            ?? agentsState.Agents.FirstOrDefault();
        if (agent is null) return false;

        await AppServices.Agents.SetSelectedAgentAsync(agent.Id);

        // No session is created here. AgentDetailPage is the start-a-chat surface and creates one
        // when the user actually sends their first message, so a shortcut pressed by accident
        // leaves nothing behind — the sidebar used to collect an empty conversation per stray
        // Same path the agent row's "+" takes (ShellSidebar.Events.AddChat_Click), so the sidebar's
        // two entry points into "new chat" behave identically.
        RevealAgentInSidebar(agent.Id);
        // forceReload because the target may already be the current page: a plain Navigate to the
        // page you are on is a no-op, and the composer would keep whatever was on it.
        RequestNavigation(typeof(AgentDetailPage), forceReload: true);
        return true;
    }

    private void OnPresenceChanged(string agentId)
    {
        if (Volatile.Read(ref _shutdown) != 0) return;

        // Fired from the app-level presence service, possibly on a background thread and
        // possibly after this control left the visual tree. Guard the dispatcher: a null
        // one means there is no live UI to update, so there is nothing to do.
        var dispatcher = _dispatcher ?? DispatcherQueue;
        if (dispatcher is null) return;

        dispatcher.TryEnqueue(() =>
        {
            // Shutdown may have started after TryEnqueue accepted this callback.
            if (Volatile.Read(ref _shutdown) != 0) return;
            if (FindShellAgent(agentId) is { } agent)
            {
                agent.Presence = _presence.GetPresence(agentId);
            }
            UpdateOnlineAgentCount();
        });
    }

    private void UpdateOnlineAgentCount()
        => OnlineAgentCount = ShellAgents.Count(agent => _presence.GetPresence(agent.Id) == AgentPresence.Online);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void RequestNavigation(Type page, bool forceReload = false)
        => NavigationRequested?.Invoke(page, forceReload);

    private void RequestNavigationReset(Type page)
        => NavigationResetRequested?.Invoke(page);

    /// <remarks>
    /// Does not populate <see cref="ShellSessionItem.AgentName"/>: no row template in this control
    /// renders it. It used to sit under a pinned conversation's title and was removed with the rest
    /// of the two-line row treatment, but the parameter that fed it outlived the thing that drew
    /// it — and on the paged load paths it meant reading every agent (and every agent's cached
    /// <c>/info</c> blob) to fill a field nothing displays. The one surface that does show an
    /// owning-agent name is <c>SessionSearchOverlay</c>, which sets it from its own catalog.
    /// </remarks>
    private ShellSessionItem CreateShellSessionItem(
        SessionSummary session,
        string? activeSessionId)
    {
        var updatedAt = ParseUpdatedAt(session.UpdatedAt);
        var preview = _sessionPreviewCache.TryGetValue(session.Id, out var cached)
            ? cached.Preview
            : "No messages yet";

        return new()
        {
            Id = session.Id,
            Title = session.Title,
            Preview = preview,
            TimeGroup = FormatTimeGroup(updatedAt),
            UpdatedAtLabel = FormatCompactTimestamp(updatedAt),
            UnreadCount = session.UnreadCount,
            RequiresAttention = session.RequiresAttention,
            IsPinned = session.IsPinned,
            // Both the pinned shortcut and the canonical tree row represent the current
            // conversation, so both carry the current-location accent.
            IsSelected = IsSessionSelected(session.Id, activeSessionId),
        };
    }

    /// <param name="prune">Whether entries outside <paramref name="sessions"/> are evicted. True
    /// only for the full refresh, where <paramref name="sessions"/> really is everything currently
    /// rendered. An incremental page load passes just the page that arrived, and pruning against
    /// that would throw away the previews for every row already on screen — they would be refetched
    /// on the next refresh, and in the meantime the render signature would move for a tree that had
    /// not changed.</param>
    private async System.Threading.Tasks.Task RefreshSessionPreviewCacheAsync(
        IReadOnlyCollection<SessionSummary> sessions,
        bool prune = true)
    {
        if (prune)
        {
            var currentIds = sessions.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var staleId in _sessionPreviewCache.Keys.Where(id => !currentIds.Contains(id)).ToArray())
            {
                _sessionPreviewCache.Remove(staleId);
            }
        }

        var refresh = sessions
            .Where(session => !_sessionPreviewCache.TryGetValue(session.Id, out var cached)
                              || !string.Equals(cached.UpdatedAt, session.UpdatedAt, StringComparison.Ordinal))
            .ToArray();
        var lastMessages = await AppServices.Conversations.LoadLastMessagesAsync(
            refresh.Select(session => session.Id).ToArray());

        foreach (var session in refresh)
        {
            lastMessages.TryGetValue(session.Id, out var lastMessage);
            _sessionPreviewCache[session.Id] = (session.UpdatedAt, BuildMessagePreview(lastMessage));
        }
    }

    private static string BuildMessagePreview(ChatMessage? message)
    {
        if (message is null) return "No messages yet";

        var value = !string.IsNullOrWhiteSpace(message.Content)
            ? message.Content
            : !string.IsNullOrWhiteSpace(message.EventTitle)
                ? message.EventTitle
                : message.EventDetail;
        if (string.IsNullOrWhiteSpace(value)) return "Activity update";

        var normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 72 ? normalized : $"{normalized[..71]}…";
    }

    private static DateTimeOffset? ParseUpdatedAt(string value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToLocalTime() : null;

    private static string FormatTimeGroup(DateTimeOffset? updatedAt)
    {
        if (updatedAt is null) return "Earlier";

        var date = updatedAt.Value.Date;
        var today = DateTimeOffset.Now.Date;
        if (date == today) return "Today";
        if (date == today.AddDays(-1)) return "Yesterday";
        return "Earlier";
    }

    private static string FormatCompactTimestamp(DateTimeOffset? updatedAt)
    {
        if (updatedAt is null) return "";
        var local = updatedAt.Value;
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("MMM d", CultureInfo.CurrentCulture);
    }

    /// <summary>Whether an agent row renders as selected. Selection is page-dependent: an agent
    /// is only "current" while the user is on a page that is actually about an agent, so
    /// Settings or Home leaves the tree with nothing highlighted rather than pointing at
    /// whichever agent happened to be selected last.</summary>
    private bool IsAgentSelected(string agentId, string? selectedAgentId)
        => _currentPageType == typeof(AgentDetailPage) && agentId == selectedAgentId;

    private bool IsSessionSelected(string sessionId, string? activeSessionId)
        => _currentPageType == typeof(ChatPage) && sessionId == activeSessionId;

    /// <summary>Captures the live expansion state before a rebuild discards the items.</summary>
    private void RememberExpandedAgentsFromUi()
    {
        // An empty tree carries no information — this is the pre-first-refresh case, and
        // clearing here would wipe an expansion another method just set (see RevealAgentInSidebar).
        if (ShellAgents.Count == 0) return;

        _expandedAgentIds.Clear();
        foreach (var agent in ShellAgents.Where(agent => agent.IsExpanded))
        {
            _expandedAgentIds.Add(agent.Id);
        }
        _hasExpansionState = true;
    }

    /// <summary>Reveals one agent without disturbing any other branches the user has open.
    /// Every navigation path uses this additive behavior so selecting an agent, conversation,
    /// or new-chat entry never discards expansion choices made elsewhere in the tree.</summary>
    private void RevealAgentInSidebar(string agentId)
    {
        _expandedAgentIds.Add(agentId);
        var agent = FindShellAgent(agentId);
        if (agent is not null)
        {
            agent.IsExpanded = true;
            // Same reason as the disclosure handlers: with a flat row list, expanding has to put
            // the agent's conversation rows into the list. A caller that follows this with a
            // RefreshAsync would rebuild anyway, but several do not (the tree is already current
            // and only the expansion changed), and those left the agent looking expanded with
            // nothing under it.
            RebuildSidebarRows();
            // And with paging, a branch that has never been opened has no rows to render yet.
            if (!agent.SessionsLoaded) _ = LoadFirstPageAsync(agent);
        }
        _hasExpansionState = true;
        QueueSidebarStateSave();
    }

    /// <summary>
    /// Snapshots the UI state before leaving the UI thread. The repository coalesces overlapping
    /// writes so rapid disclosure clicks still persist the last visible state.
    /// </summary>
    private void QueueSidebarStateSave()
    {
        var expandedAgentIds = _expandedAgentIds.ToArray();
        var isPinnedExpanded = IsPinnedExpanded;
        _ = SaveSidebarStateAsync(expandedAgentIds, isPinnedExpanded);
    }

    private static async System.Threading.Tasks.Task SaveSidebarStateAsync(
        string[] expandedAgentIds,
        bool isPinnedExpanded)
    {
        try
        {
            await AppServices.SidebarState
                .SaveAsync(expandedAgentIds, isPinnedExpanded)
                .ConfigureAwait(false);
        }
        catch
        {
            // Disclosure remains usable in memory if persistence is temporarily unavailable.
        }
    }

    /// <summary>Guarantees the agent has an active conversation, reusing the current one if it
    /// already belongs to this agent, else its most recent, else creating one.</summary>
    private async System.Threading.Tasks.Task<string> EnsureSessionAsync(string agentId)
    {
        // Three targeted reads at most, where this used to load every conversation the user has
        // ever had in order to answer "does this agent have one".
        var activeId = await AppServices.Sessions.GetActiveSessionIdAsync();
        if (activeId is not null)
        {
            var active = await AppServices.Sessions.GetSessionAsync(activeId);
            if (active?.AgentId == agentId) return active.Id;
        }

        var recent = await AppServices.Sessions.LoadAgentSessionsAsync(agentId, limit: 1);
        if (recent.Sessions.Count > 0)
        {
            await AppServices.Sessions.SetActiveSessionAsync(recent.Sessions[0].Id);
            return recent.Sessions[0].Id;
        }

        return await CreateAndActivateSessionAsync(agentId);
    }

    /// <summary>Appends a fresh "Conversation N" session for the agent, makes it active, persists,
    /// and returns its id. The number comes from a <c>COUNT(*)</c> rather than from counting a
    /// loaded index — that count was the only thing the whole list was ever read for here.</summary>
    private static async System.Threading.Tasks.Task<string> CreateAndActivateSessionAsync(string agentId)
    {
        var existingCount = await AppServices.Sessions.CountForAgentAsync(agentId);
        var session = SessionSummary.NewConversation(agentId, existingCount, Common.SessionTitles.PlaceholderFormat);
        // One INSERT plus the active pointer, not a whole-index reconcile — see AppendSessionAsync.
        await AppServices.Sessions.AppendSessionAsync(session);
        return session.Id;
    }

    private async System.Threading.Tasks.Task SelectAgentAsync(string agentId)
    {
        await AppServices.Agents.SetSelectedAgentAsync(agentId);
        RequestNavigation(typeof(ChatPage), forceReload: true);
    }

    /// <summary>Opens a conversation, moving agent selection with it (a pinned row can belong to
    /// an agent other than the current one) while preserving every branch the user has open.</summary>
    private async System.Threading.Tasks.Task SelectSessionAsync(string sessionId)
    {
        // Every pinned row also has a canonical copy under its owning agent, even while that
        // branch is collapsed. Resolve the owner from the already-rendered tree instead of
        // re-reading the complete session index before the click can do anything visible.
        var agentId = ShellAgents
            .FirstOrDefault(agent => agent.Sessions.Any(session => session.Id == sessionId))
            ?.Id;
        if (agentId is null) return;

        var selectionGeneration = Interlocked.Increment(ref _sessionSelectionGeneration);
        ApplyOptimisticSessionSelection(sessionId);

        try
        {
            // Microsoft.Data.Sqlite delegates to SQLite's synchronous API. Running these small
            // metadata writes on the worker pool lets WinUI render the optimistic selection in
            // the click frame instead of holding it behind connection open + two writes.
            if (!await QueueSessionSelectionPersistenceAsync(
                    sessionId,
                    agentId,
                    selectionGeneration)) return;

            RevealAgentInSidebar(agentId);
            RequestNavigation(typeof(ChatPage), forceReload: true);
        }
        catch
        {
            if (IsCurrentSessionSelection(selectionGeneration))
            {
                // The optimistic paint must not survive a failed write. Force an authoritative
                // rebuild even if the storage revision did not move before the exception.
                _lastLoadedRevision = -1;
                _lastStructureSignature = null;
                try { await RefreshAsync(); }
                catch { /* best-effort rollback; persistence failure remains non-fatal */ }
            }
        }
    }

    /// <summary>
    /// Repaints selection onto the items already on screen, from the authoritative pointers.
    ///
    /// <para>The counterpart to <see cref="ApplyOptimisticSessionSelection"/>, which paints the
    /// click's <i>intent</i> before storage confirms it. This one applies what storage actually
    /// says, including the cases that one cannot express: an agent row lit on
    /// <c>AgentDetailPage</c>, and nothing lit at all on Home or Settings. Both go through
    /// <see cref="IsAgentSelected"/>/<see cref="IsSessionSelected"/>, so the result is identical
    /// to what a full rebuild would have produced — that equivalence is what makes skipping the
    /// rebuild safe.</para>
    /// </summary>
    private void ApplySelection(string? selectedAgentId, string? activeSessionId)
    {
        foreach (var session in PinnedSessions)
            session.IsSelected = IsSessionSelected(session.Id, activeSessionId);

        foreach (var agent in ShellAgents)
        {
            agent.IsSelected = IsAgentSelected(agent.Id, selectedAgentId);
            // Session groups hold these same instances, so the canonical row and its pinned
            // shortcut both update from the one pass.
            foreach (var session in agent.Sessions)
                session.IsSelected = IsSessionSelected(session.Id, activeSessionId);
        }
    }

    /// <summary>Updates both representations of a conversation in place. Session groups contain
    /// these same item instances, so this changes the visible accent without clearing a collection
    /// or triggering a new XAML measure/arrange pass for every sidebar row.</summary>
    private void ApplyOptimisticSessionSelection(string sessionId)
    {
        foreach (var session in PinnedSessions)
            session.IsSelected = session.Id == sessionId;

        foreach (var agent in ShellAgents)
        {
            // A conversation is now the pending destination, so an Agent Detail row must stop
            // presenting itself as the current location immediately as well.
            agent.IsSelected = false;
            foreach (var session in agent.Sessions)
                session.IsSelected = session.Id == sessionId;
        }
    }

    private bool IsCurrentSessionSelection(long selectionGeneration)
        => Volatile.Read(ref _shutdown) == 0
           && selectionGeneration == Interlocked.Read(ref _sessionSelectionGeneration);

    private System.Threading.Tasks.Task<bool> QueueSessionSelectionPersistenceAsync(
        string sessionId,
        string agentId,
        long selectionGeneration)
    {
        lock (_sessionSelectionQueueGate)
        {
            var queued = PersistSessionSelectionAfterAsync(
                _sessionSelectionTail,
                sessionId,
                agentId,
                selectionGeneration);
            _sessionSelectionTail = queued;
            return queued;
        }
    }

    private System.Threading.Tasks.Task<bool> PersistSessionSelectionAfterAsync(
        System.Threading.Tasks.Task predecessor,
        string sessionId,
        string agentId,
        long selectionGeneration)
    {
        var sessions = AppServices.Sessions;
        var agents = AppServices.Agents;
        // ContinueWith runs even when the predecessor faulted (its own SelectSessionAsync caller
        // observes that failure), so one failed click cannot poison the queue. TaskScheduler.Default
        // also guarantees the synchronous SQLite work never resumes on WinUI's dispatcher.
        return System.Threading.Tasks.TaskExtensions.Unwrap(
            predecessor.ContinueWith(
                async _ =>
                {
                    if (!IsCurrentSessionSelection(selectionGeneration)) return false;
                    await sessions.SetActiveSessionAsync(sessionId).ConfigureAwait(false);
                    await agents.SetSelectedAgentAsync(agentId).ConfigureAwait(false);
                    return IsCurrentSessionSelection(selectionGeneration);
                },
                CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.None,
                System.Threading.Tasks.TaskScheduler.Default));
    }

    private ShellAgentItem? FindShellAgent(string agentId)
        => ShellAgents.FirstOrDefault(agent => agent.Id == agentId);

    /// <summary>Shows the shared Delete/Cancel confirmation dialog; true if the
    /// user confirmed deletion.</summary>
    private async System.Threading.Tasks.Task<bool> ConfirmDeleteAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = LocalizedStrings.Get("CommonDelete", "Delete"),
            CloseButtonText = LocalizedStrings.Get("CommonCancel", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowThemedAsync() == ContentDialogResult.Primary;
    }

    private void ChatHome_Click(object sender, RoutedEventArgs e)
    {
        RequestNavigation(typeof(HomePage), forceReload: true);
    }

    private async void Agent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string agentId)
        {
            await AppServices.Agents.SetSelectedAgentAsync(agentId);
            RevealAgentInSidebar(agentId);
            RequestNavigation(typeof(AgentDetailPage), forceReload: true);
        }
    }

    private void ToggleAgent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string agentId &&
            FindShellAgent(agentId) is { } agent)
        {
            agent.IsExpanded = !agent.IsExpanded;
            if (agent.IsExpanded)
            {
                _expandedAgentIds.Add(agentId);
            }
            else
            {
                _expandedAgentIds.Remove(agentId);
            }
            _hasExpansionState = true;
            // The rows are a flat list, so disclosure adds or removes this agent's conversation
            // rows rather than toggling a Visibility on a nested repeater.
            RebuildSidebarRows();
            QueueSidebarStateSave();
            // A collapsed agent is never fetched, so the first expansion is also the first read of
            // its conversations. Rows above are already on screen; this fills the branch in when it
            // lands rather than holding the disclosure behind a query.
            if (agent.IsExpanded && !agent.SessionsLoaded) _ = LoadFirstPageAsync(agent);
        }
    }

    private async void ShowMoreSessions_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string agentId) return;

        // The row disables itself while its page is in flight, so a slow disk cannot let repeat
        // clicks queue several pages. It is found by identity in the row list because the row is
        // the thing that has to show the state.
        var row = SidebarRows.OfType<ShellShowMoreSessionsRow>()
            .FirstOrDefault(candidate => candidate.AgentId == agentId);
        if (row is { IsLoading: true }) return;
        if (row is not null) row.IsLoading = true;

        try
        {
            await LoadMoreSessionsAsync(agentId);
        }
        finally
        {
            // The rebuild replaces the row, so this only matters when the load failed or was
            // superseded and the old row is still on screen.
            if (row is not null) row.IsLoading = false;
        }
    }

    private void TogglePinned_Click(object sender, RoutedEventArgs e)
    {
        IsPinnedExpanded = !IsPinnedExpanded;
        RebuildSidebarRows();
        QueueSidebarStateSave();
    }

    /// <summary>Honours the OS "show animations" accessibility setting (Reduce Motion). Read once
    /// as a startup constant, matching <c>DisclosureAnimation</c>/<c>ThinkingIndicator</c> — the
    /// value only changes across an app restart, and a stale read costs at most the motion the user
    /// sees until then.</summary>
    private static readonly bool AnimationsEnabled = new UISettings().AnimationsEnabled;

    /// <summary>
    /// Starts the breathing animation on the "Connecting" presence dot once per
    /// realized Ellipse — an in-flight reachability probe pulses; a settled Online
    /// dot is solid and static. WinUI's FrameworkElement.Triggers/EventTrigger has
    /// no RoutedEvent property (WPF-only), so a XAML-declared Loaded trigger isn't
    /// valid here — the Storyboard is built and started from code instead.
    ///
    /// Under Reduce Motion the dot is left solid (full opacity): the "Connecting" state is still
    /// carried by its colour, tooltip and AutomationProperties.Name, so suppressing the pulse costs
    /// nothing but the motion.
    /// </summary>
    private void PresenceCheckingDot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Ellipse ellipse) return;
        if (!AnimationsEnabled) return;

        var animation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.35,
            Duration = TimeSpan.FromSeconds(1.2),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(animation, ellipse);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

}
