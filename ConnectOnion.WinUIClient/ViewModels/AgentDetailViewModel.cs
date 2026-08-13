using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Dispatching;

namespace ConnectOnion.WinUIClient.ViewModels;

/// <summary>
/// Backs the Agent Detail page: loads the selected agent's /info payload and
/// exposes key fields plus the raw JSON for the UI.
/// </summary>
public sealed partial class AgentDetailViewModel : PresenceAwareViewModel
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private AgentConfig? _agent;

    // Bumped by every LoadAsync. The page is reused across agents (see Views.IReloadablePage),
    // so loads can overlap; only the newest may write to it.
    private long _loadGeneration;

    private readonly AgentRepository _agents;
    private readonly SessionRepository _sessions;
    private readonly PreferencesRepository _preferences;

    public AgentDetailViewModel(
        AgentPresenceService presence,
        AgentRepository agents,
        SessionRepository sessions,
        PreferencesRepository preferences)
        : base(presence)
    {
        _agents = agents;
        _sessions = sessions;
        _preferences = preferences;

        // Non-default seeds for the partial [ObservableProperty] declarations below.
        InfoJson = "";
        StatusText = "";
        EnterToSend = true;
        CurrentMode = AgentModes.Safe;
    }

    /// <summary>The agent this page tracks for presence (base class reads this).</summary>
    protected override AgentConfig? PresenceAgent => _agent;

    /// <summary>Detaches from the shared presence service. Call when the page unloads.</summary>
    public void Cleanup() => DetachPresence();

    public AgentConfig? Agent
    {
        get => _agent;
        private set
        {
            if (_agent?.Id != value?.Id)
            {
                InfoFields.Clear();
                InfoJson = "";
                AcceptedInputs = null;
                // Cleared with the rest of the cached /info: showing the previous agent's skills
                // as this one's shortcuts would offer commands it does not have.
                Skills = null;
            }

            if (!SetProperty(ref _agent, value)) return;
            OnPropertyChanged(nameof(AgentName));
            OnPropertyChanged(nameof(AgentDisplayName));
            OnPropertyChanged(nameof(AgentAddress));
            OnPropertyChanged(nameof(DirectUrl));
            OnPropertyChanged(nameof(CanShareAgent));
            OnPropertyChanged(nameof(HasAgent));
            OnPropertyChanged(nameof(AgentInitial));
            OnPropertyChanged(nameof(AgentIconPath));
            RaisePresenceProperties();
            RaiseProfileSummaryProperties();
        }
    }

    public string AgentName => _agent?.Name ?? "";
    public string AgentDisplayName => _agent?.DisplayName ?? "";
    public string AgentInitial => NameInitial.From(AgentDisplayName);

    /// <summary>The agent's chosen icon, relative to the data root. Null falls back to
    /// <see cref="AgentInitial"/> on a theme-neutral background.</summary>
    public string? AgentIconPath => _agent?.IconPath;

    public string AgentAddress => _agent?.Address ?? "";
    public string? DirectUrl => _agent?.DirectUrl;
    public bool CanShareAgent => _agent?.IsRelayOnly == true;
    public bool HasAgent => _agent is not null;
    public string ComposerPlaceholder => string.IsNullOrWhiteSpace(AgentDisplayName)
        ? LocalizedStrings.Get("ComposerAskAgent", "Ask this agent anything")
        : LocalizedStrings.Format("ComposerAskNamedAgent", "Ask {0} anything", AgentDisplayName);

    public bool CanStartConversation => HasAgent && IsOnline;

    /// <summary>The approval mode the first message will start its conversation under.
    ///
    /// <para>Held here rather than on a session because there is no session yet — this page is what
    /// creates one. It rides across on <see cref="Controls.ComposerSubmission.Mode"/> and
    /// <c>ChatPage</c> applies it to the conversation before the first send, so the picker on this
    /// page means the same thing as the picker on the chat page.</para>
    ///
    /// <para>Not persisted per agent. Mode is conversation-owned state
    /// (<c>sessions.mode</c>), and inferring a default for a conversation that does not exist yet
    /// from an earlier, unrelated one would be a guess about intent — <c>Safe</c> is the honest
    /// starting point, and it is one click away from the others.</para></summary>
    [ObservableProperty]
    public partial string CurrentMode { get; set; }

    /// <summary>Also refresh whether a conversation can be started (gated on online).</summary>
    protected override void OnPresenceRaised()
        => OnPropertyChanged(nameof(CanStartConversation));

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfoJson))]
    public partial string InfoJson { get; private set; }

    public bool HasInfoJson => !string.IsNullOrWhiteSpace(InfoJson);

    [ObservableProperty]
    public partial string StatusText { get; private set; }

    [ObservableProperty]
    public partial bool HasError { get; private set; }

    public ObservableCollection<InfoField> InfoFields { get; } = new();

    /// <summary>The agent's model, exactly as it reported it — empty when <c>/info</c> did not
    /// say. Never guesses: this label sits under the agent's name and reads as fact, so naming
    /// a model the agent may not be running is worse than showing nothing. The row hides itself
    /// via <see cref="HasDisplayModel"/> instead.</summary>
    public string DisplayModel => GetInfoValue("model") ?? "";

    public bool HasDisplayModel => !string.IsNullOrWhiteSpace(DisplayModel);
    public string Version => GetInfoValue("version") ?? "";

    /// <summary>
    /// Provenance shown under the identity block. Version only — <c>/info</c>'s <c>trust</c> is
    /// deliberately not surfaced: agents report it as an absolute path to a policy file, so it
    /// rendered as the widest line on the page, middle-truncated to
    /// <c>…/remote-admin-agent/.co/trust-pol…</c>, competing with the agent's own name while
    /// saying nothing a reader could act on.
    /// </summary>
    public string ProfileDetail
        => string.IsNullOrWhiteSpace(Version) ? "" : $"v{Version}";
    public bool HasProfileDetail => !string.IsNullOrWhiteSpace(ProfileDetail);

    public bool HasToolSummary => ToolNames.Count > 0;
    public int ToolCount => ToolNames.Count;
    public string ModelAndToolCount
    {
        get
        {
            var toolLabel = $"{ToolCount} {(ToolCount == 1 ? "tool" : "tools")}";
            return HasDisplayModel ? $"{DisplayModel} · {toolLabel}" : toolLabel;
        }
    }

    public ObservableCollection<string> ToolNames { get; } = new();
    public string ToolsDisplayText => LocalizedStrings.Format(
        "AgentDetailToolsList",
        "Tools: {0}",
        string.Join(" · ", ToolNames));
    public string ToolsToggleLabel
        => ToolCount == 1
            ? LocalizedStrings.Get("AgentDetailViewOneTool", "View 1 tool")
            : LocalizedStrings.Format(
                "AgentDetailViewTools",
                "View {0} tools",
                ToolCount);

    public string ShortAddress
        => string.IsNullOrWhiteSpace(AgentAddress)
            ? ""
            : AgentAddress.Length >= 10
                ? $"{AgentAddress[..6]}...{AgentAddress[^4..]}"
                : AgentAddress;

    public string? LastUpdated => Agent?.InfoUpdatedAt is { } ts
        ? $"Last updated {ts}"
        : null;

    [ObservableProperty]
    public partial bool EnterToSend { get; private set; }

    /// <summary>Same purpose as <c>ChatViewModel.AcceptedInputs</c>: gates the composer's attachment UI against the agent's real <c>/info</c> capability.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AcceptedInputsSummary))]
    [NotifyPropertyChangedFor(nameof(HasAcceptedInputsSummary))]
    public partial AgentAcceptedInputs? AcceptedInputs { get; private set; }

    public string AcceptedInputsSummary
    {
        get
        {
            if (AcceptedInputs is null) return "";
            var parts = new List<string>();
            if (AcceptedInputs.Text == true)
                parts.Add(LocalizedStrings.Get("AgentInputText", "text"));
            if (AcceptedInputs.Images == true)
                parts.Add(LocalizedStrings.Get("AgentInputImages", "images"));
            if (AcceptedInputs.Files is { } files)
            {
                parts.Add(LocalizedStrings.Format(
                    "AgentInputFiles",
                    "files ({0} MB, up to {1})",
                    files.MaxFileSizeMb,
                    files.MaxFilesPerRequest));
            }
            return parts.Count == 0
                ? LocalizedStrings.Get("AgentNoDeclaredInputs", "No declared inputs")
                : LocalizedStrings.Format(
                    "AgentAcceptsInputs",
                    "Accepts {0}",
                    string.Join(" · ", parts));
        }
    }
    public bool HasAcceptedInputsSummary => AcceptedInputs is not null;

    /// <summary>The agent's declared skills, from the same cached <c>/info</c> as
    /// <see cref="AcceptedInputs"/>. Feeds the composer's slash palette and, through
    /// <see cref="SkillOffers"/>, this page's opening suggestions.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SkillOffers))]
    [NotifyPropertyChangedFor(nameof(HasSkillOffers))]
    public partial IReadOnlyList<SkillInfo>? Skills { get; private set; }

    /// <summary>
    /// Up to three of the agent's skills phrased as things to ask for. The composer puts these
    /// first, then fills any unused slots with its generic starters.
    ///
    /// <para>Empty is a normal outcome, not a failure: an agent whose descriptions will not cut
    /// into clean short offers yields nothing and the static chips fill the row. A chip is the
    /// first thing a user reads about an agent, so a bad one is worse than none (see
    /// <see cref="AgentSkills.BestOffers"/>).</para>
    ///
    /// <para>Memoized against <see cref="Skills"/>, which changes at most once per page (the
    /// cached <c>/info</c> arrives). It used to recompute on every read — and every read came in
    /// pairs, because <see cref="HasSkillOffers"/> asked for the whole list just to test its
    /// count. <see cref="AgentSkills.BestOffers"/> is not free: it filters, cuts each description
    /// at a clause boundary, fixes brand casing and ranks the survivors.</para>
    /// </summary>
    public IReadOnlyList<string> SkillOffers
    {
        get
        {
            // Keyed on the source reference rather than invalidated from an OnSkillsChanged
            // hook, so this does not depend on where the MVVM generator happens to place that
            // callback relative to the NotifyPropertyChangedFor notification. If the ordering
            // were wrong that way, a binding would read the previous agent's chips exactly once
            // — the kind of staleness that shows up in a screenshot and nowhere else.
            if (_skillOffers is null || !ReferenceEquals(_skillOffersSource, Skills))
            {
                _skillOffersSource = Skills;
                _skillOffers = AgentSkills.BestOffers(Skills);
            }
            return _skillOffers;
        }
    }

    private IReadOnlyList<string>? _skillOffers;
    private IReadOnlyList<SkillInfo>? _skillOffersSource;

    public bool HasSkillOffers => SkillOffers.Count > 0;

    /// <summary>
    /// Starts a new conversation. <paramref name="hasAttachments"/> must be true
    /// when the composer submission carries attachments even if
    /// <paramref name="prompt"/> is empty — an image/file-only send is valid and
    /// must still create and navigate to a session, matching ChatPage's own
    /// composer (previously this only checked the text, so an attachment-only
    /// submission here silently did nothing).
    /// </summary>
    public async Task<bool> StartConversationAsync(string prompt, bool hasAttachments = false)
    {
        if (Agent is null || (string.IsNullOrWhiteSpace(prompt) && !hasAttachments)) return false;
        if (!IsOnline)
        {
            HasError = true;
            StatusText = LocalizedStrings.Get(
                "AgentDetailOfflineError",
                "The agent is not online. Recheck the connection before starting a conversation.");
            return false;
        }

        try
        {
            HasError = false;
            StatusText = "";

            // Note what this does *not* do: it creates and selects the session but never sends the
            // prompt. The caller navigates to ChatPage, which picks up the prompt and sends it —
            // sending here would race the page that is about to own the conversation.
            await _agents.SetSelectedAgentAsync(Agent.Id);

            // The conversation number is all the index was read for; COUNT(*) answers it directly.
            var existingCount = await _sessions.CountForAgentAsync(Agent.Id);
            var session = SessionSummary.NewConversation(Agent.Id, existingCount, Common.SessionTitles.PlaceholderFormat);
            await _sessions.AppendSessionAsync(session);
            return true;
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusText = LocalizedStrings.Format(
                "AgentDetailStartFailure",
                "Could not start the conversation: {0}",
                ex.Message);
            return false;
        }
    }

    /// <summary>Loads agent info from cache or fetches fresh data.</summary>
    public async Task LoadAsync()
    {
        // Switching agents reuses this page rather than rebuilding it, so two loads can be in
        // flight at once — and an /info fetch is slow enough to outlive the agent that asked for
        // it. Only the newest load may write to the page; a superseded one drops out here, and
        // its fetch drops out where it would otherwise publish (FetchInfoWithCacheAsync).
        var generation = ++_loadGeneration;

        // Loading belongs to one generation, not to the cached page instance. A previous agent
        // may still have an /info request in flight; the new load must clear its spinner before
        // taking an early return for a missing agent or a fresh cache hit.
        IsLoading = false;

        var prefsTask = _preferences.LoadAsync();
        var agentTask = _agents.GetSelectedAgentAsync();
        await Task.WhenAll(prefsTask, agentTask);
        var prefs = prefsTask.Result;
        if (generation != _loadGeneration) return;
        EnterToSend = prefs.EnterToSend;

        if (generation != _loadGeneration) return;

        var selected = agentTask.Result;

        Agent = selected;
        if (Agent is null) return;

        // Probe once on open; the offline bar stays hidden until it resolves so a
        // stale cached "offline" never flashes the reconnect UI. Placed before the
        // /info early-returns below so it always runs.
        _ = ProbePresenceOnOpenAsync();

        // Use cached /info immediately; only fetch when the cache is missing or stale.
        if (!string.IsNullOrWhiteSpace(Agent.InfoJson))
        {
            PopulateFromCachedJson(Agent.InfoJson);
            StatusText = LocalizedStrings.Format("AgentDetailCached", "Cached — {0}", LastUpdated);
            if (AgentInfoService.IsCacheFresh(Agent)) return;
        }

        IsLoading = true;
        await FetchInfoWithCacheAsync(generation);
    }

    private async Task FetchInfoWithCacheAsync(long generation)
    {
        var agent = Agent;
        if (agent is null)
        {
            IsLoading = false;
            return;
        }

        var hadCache = !string.IsNullOrWhiteSpace(agent.InfoJson);
        string? freshJson;
        try
        {
            freshJson = await AgentInfoService.FetchAndPersistAsync(agent, forceRefresh: true);
        }
        catch
        {
            // Network helpers already translate ordinary reachability failures to null, but
            // persistence or shutdown can still throw. Treat those exactly like a failed fetch
            // so the page leaves its loading state and exposes the retryable error UI.
            freshJson = null;
        }

        _dispatcher.TryEnqueue(() =>
        {
            // The page has moved on to another agent; publishing now would show one agent's info
            // under another's name. IsLoading belongs to the newer load, so don't clear it either.
            if (generation != _loadGeneration) return;

            try
            {
                if (!string.IsNullOrWhiteSpace(freshJson))
                {
                    HasError = false;
                    PopulateFromCachedJson(freshJson);
                    OnPropertyChanged(nameof(LastUpdated));
                    StatusText = LocalizedStrings.Format("AgentDetailUpdated", "Updated - {0}", LastUpdated);
                }
                else if (hadCache)
                {
                    HasError = false;
                    StatusText = LocalizedStrings.Format("AgentDetailCachedDash", "Cached - {0}", LastUpdated);
                }
                else
                {
                    InfoFields.Clear();
                    InfoJson = "";
                    HasError = true;
                    StatusText = LocalizedStrings.Get("AgentDetailLoadFailure", "Could not load agent info.");
                }
            }
            finally
            {
                // Keep this in finally: malformed metadata or a future projection failure must
                // never strand the capabilities ProgressRing.
                IsLoading = false;
            }
        });
    }

    /// <summary>
    /// Populates <see cref="InfoFields"/> from a cached JSON string,
    /// using the same recursive flattening as the Direct URL path.
    /// </summary>
    private void PopulateFromCachedJson(string json)
    {
        InfoFields.Clear();
        InfoJson = json;
        try
        {
            using var doc = JsonDocument.Parse(json);
            FlattenObject(doc.RootElement, "", InfoFields);
            AcceptedInputs = EndpointResolver.ParseAcceptedInputs(doc.RootElement);
            Skills = EndpointResolver.ParseSkills(doc.RootElement);
            RaiseProfileSummaryProperties();
        }
        catch
        {
            // Corrupt cache: leave fields empty, a refresh will fix it.
            AcceptedInputs = null;
            Skills = [];
        }
    }

    /// <summary>
    /// Recursively flattens nested objects/arrays into a flat key-value list.
    /// Nested keys use dot notation: <c>accepted_inputs.files.max_file_size_mb</c>.
    /// Arrays of simple values are comma-joined; arrays of objects are expanded
    /// with index prefixes.
    /// </summary>
    private static void FlattenObject(
        JsonElement element,
        string prefix,
        ObservableCollection<InfoField> fields)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                    var value = prop.Value;
                    if (value.ValueKind is JsonValueKind.Object
                        or JsonValueKind.Array)
                    {
                        FlattenObject(value, key, fields);
                    }
                    else
                    {
                        fields.Add(new InfoField(key, ElementToString(value)));
                    }
                }
                break;

            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToList();
                if (items.Count == 0)
                {
                    fields.Add(new InfoField(prefix, "[empty]"));
                    return;
                }

                // If every element is a simple value, join them.
                if (items.All(it => it.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)))
                {
                    var joined = string.Join(", ", items.Select(ElementToString));
                    fields.Add(new InfoField(prefix, joined));
                    return;
                }

                // Mixed or object array: expand each item.
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var indexedPrefix = $"{prefix}[{i}]";
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        FlattenObject(item, indexedPrefix, fields);
                    }
                    else
                    {
                        fields.Add(new InfoField(indexedPrefix, ElementToString(item)));
                    }
                }
                break;
        }
    }

    private void RaiseProfileSummaryProperties()
    {
        RefreshToolNames();
        OnPropertyChanged(nameof(DisplayModel));
        OnPropertyChanged(nameof(HasDisplayModel));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(ProfileDetail));
        OnPropertyChanged(nameof(HasProfileDetail));
        OnPropertyChanged(nameof(HasToolSummary));
        OnPropertyChanged(nameof(ToolCount));
        OnPropertyChanged(nameof(ModelAndToolCount));
        OnPropertyChanged(nameof(ToolsDisplayText));
        OnPropertyChanged(nameof(ToolsToggleLabel));
        OnPropertyChanged(nameof(ComposerPlaceholder));
    }

    private void RefreshToolNames()
    {
        var names = GetToolNames().Select(FormatToolName).ToList();
        ToolNames.Clear();
        foreach (var name in names) ToolNames.Add(name);
    }

    private string? GetInfoValue(string name)
        => InfoFields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>Recovers the tool names from the flattened <c>/info</c> fields, which is
    /// necessarily shape-guessing: agents publish tools either as an array of strings (flattened
    /// to one comma-joined <c>tools</c> field) or as an array of objects (flattened to indexed
    /// <c>tools[0].name</c> keys). Both are handled; anything else yields an empty list, and the
    /// summary row simply hides.</summary>
    private List<string> GetToolNames()
    {
        // String-array shape: FlattenObject already joined it into one field.
        var direct = GetInfoValue("tools");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(tool => tool.Length > 0)
                .ToList();
        }

        // Object-array shape. The predicate takes either "tools[0].name" or a bare "tools[0]"
        // and skips every other nested property, so a tool's description or parameters cannot
        // be mistaken for a tool name.
        return InfoFields
            .Where(field => field.Name.StartsWith("tools[", StringComparison.OrdinalIgnoreCase)
                            && (field.Name.EndsWith(".name", StringComparison.OrdinalIgnoreCase)
                                || !field.Name.Contains("].", StringComparison.OrdinalIgnoreCase)))
            .Select(field => field.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FormatToolName(string value)
    {
        var normalized = value.Replace('_', ' ').Replace('-', ' ').Trim();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized.ToLowerInvariant());
    }

    /// <summary>Renders a leaf JSON value for the raw-fields table. Numbers go through
    /// <c>GetRawText</c> so they display exactly as the agent wrote them — parsing to double
    /// would round large integers and add or drop trailing zeros.</summary>
    private static string ElementToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "—",
        _ => "—",
    };
}

/// <summary>A named field from the /info response payload.</summary>
public sealed record InfoField(string Name, string Value);
