using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// A locally indexed conversation belonging to one saved agent. Mirrors the
/// TypeScript <c>SessionSummary</c> type. Timestamps are ISO-8601 strings.
/// Observable so touch/rename updates reflect in the list live.
/// </summary>
public sealed partial class SessionSummary : Common.ObservableObject
{
    public SessionSummary()
    {
        Id = "";
        AgentId = "";
        Title = "";
        CreatedAt = "";
        UpdatedAt = "";
    }

    [ObservableProperty]
    [JsonPropertyName("id")]
    public partial string Id { get; set; }

    [ObservableProperty]
    [JsonPropertyName("agentId")]
    public partial string AgentId { get; set; }

    [ObservableProperty]
    [JsonPropertyName("title")]
    public partial string Title { get; set; }

    [ObservableProperty]
    [JsonPropertyName("remoteSessionId")]
    public partial string? RemoteSessionId { get; set; }

    [ObservableProperty]
    [JsonPropertyName("lastProcessedEventId")]
    public partial string? LastProcessedEventId { get; set; }

    [ObservableProperty]
    [JsonPropertyName("createdAt")]
    public partial string CreatedAt { get; set; }

    [ObservableProperty]
    [JsonPropertyName("updatedAt")]
    public partial string UpdatedAt { get; set; }

    [ObservableProperty]
    [JsonPropertyName("isPinned")]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    [JsonPropertyName("unreadCount")]
    public partial int UnreadCount { get; set; }

    [ObservableProperty]
    [JsonPropertyName("requiresAttention")]
    public partial bool RequiresAttention { get; set; }

    /// <summary>
    /// False while <see cref="Title"/> is still the "Conversation N" placeholder that
    /// <see cref="NewConversation"/> stamped on it; true once the title has been settled — either
    /// derived from the conversation's first message or typed by the user in a rename.
    ///
    /// This exists so that "is the title still a placeholder?" is a stored fact rather than a
    /// pattern match on the title text. The old <c>^Conversation \d+$</c> test made the
    /// placeholder untranslatable (a localized placeholder stops matching, so the conversation
    /// never picks up a real title) and made rename unsafe (a user-chosen title that happened to
    /// match would be overwritten by the next message). See schema migration v7.
    /// </summary>
    [ObservableProperty]
    [JsonPropertyName("hasCustomTitle")]
    public partial bool HasCustomTitle { get; set; }

    private string _mode = Protocol.AgentModes.Safe;

    /// <summary>
    /// The approval mode this conversation's turns run under — one of <see cref="Protocol.AgentModes"/>.
    /// Client-owned: the host will not remember our choice between turns, so it is re-asserted with
    /// every INPUT. An unknown value (a mode a future host introduced, or a hand-edited row) falls
    /// back to Safe rather than being sent on the wire, where the host would ignore it anyway.
    /// Kept hand-written because the setter normalizes the incoming value, which the
    /// <c>[ObservableProperty]</c> generator deliberately cannot express.
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, Protocol.AgentModes.IsValid(value) ? value : Protocol.AgentModes.Safe);
    }

    /// <summary>Collapses runs of whitespace so a multi-line prompt becomes a single-line title.</summary>
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Longest auto-derived or user-entered title kept, in chars.</summary>
    public const int MaxTitleLength = 48;

    /// <summary>
    /// Replaces a still-placeholder title with the conversation's opening prompt, and marks it
    /// settled. Does nothing once <see cref="HasCustomTitle"/> is true — that covers both a title
    /// already derived from the first message and one the user typed, neither of which a later
    /// message may overwrite.
    /// </summary>
    /// <returns>True if the title changed.</returns>
    public bool TryApplyTitleFromPrompt(string? prompt)
    {
        if (HasCustomTitle || string.IsNullOrWhiteSpace(prompt)) return false;

        var title = Whitespace.Replace(prompt.Trim(), " ");
        Title = title.Length > MaxTitleLength ? title[..MaxTitleLength] : title;
        HasCustomTitle = true;
        return true;
    }

    /// <summary>
    /// Applies a user-chosen title. Blank input is rejected rather than stored: an empty title is
    /// treated as a corrupt row by <see cref="Data.SessionRepository"/> and would drop the
    /// conversation out of every list on the next load.
    /// </summary>
    /// <returns>True if the title changed.</returns>
    public bool TryRename(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;

        var trimmed = Whitespace.Replace(title.Trim(), " ");
        if (trimmed.Length > MaxTitleLength) trimmed = trimmed[..MaxTitleLength];
        if (string.Equals(trimmed, Title, StringComparison.Ordinal) && HasCustomTitle) return false;

        Title = trimmed;
        HasCustomTitle = true;
        return true;
    }

    /// <summary>The untranslated placeholder shape, used when a caller supplies no format.
    /// Core cannot reach the app's resource map, so the localized wording is passed in.</summary>
    public const string DefaultTitleFormat = "Conversation {0}";

    /// <summary>
    /// Builds a fresh placeholder-titled session for an agent, numbering it after the agent's
    /// existing conversations. This is the single source of truth for new-session shape
    /// (id/title/timestamps) — callers still Add/Insert it into their list and persist.
    /// <paramref name="existing"/> is only read to compute N.
    /// </summary>
    /// <param name="titleFormat">
    /// A composite format whose <c>{0}</c> is the conversation number, e.g. "Conversation {0}".
    /// The app project passes the localized string; Core has no resource map of its own. Null or
    /// blank falls back to <see cref="DefaultTitleFormat"/>. The result is marked
    /// <see cref="HasCustomTitle"/> = false regardless of wording, which is what lets the
    /// placeholder be translated without breaking auto-titling.
    /// </param>
    public static SessionSummary NewConversation(
        string agentId,
        IEnumerable<SessionSummary> existing,
        string? titleFormat = null)
        => NewConversation(agentId, existing.Count(s => s.AgentId == agentId), titleFormat);

    /// <summary>
    /// The same, given the agent's conversation count directly.
    ///
    /// <para>The overload above is the only thing the whole session index was ever needed for at
    /// these call sites — it reduces the list to one integer. Callers that can get that integer
    /// from <c>SessionRepository.CountForAgentAsync</c> should, rather than reading every
    /// conversation the user has in order to count a subset of them.</para>
    /// </summary>
    /// <param name="existingCountForAgent">How many conversations the agent already has. The new
    /// conversation is numbered one past it.</param>
    public static SessionSummary NewConversation(
        string agentId,
        int existingCountForAgent,
        string? titleFormat = null)
    {
        var count = Math.Max(0, existingCountForAgent) + 1;
        var timestamp = DateTime.UtcNow.ToString("o");
        var format = string.IsNullOrWhiteSpace(titleFormat) ? DefaultTitleFormat : titleFormat;
        return new SessionSummary
        {
            Id = Guid.NewGuid().ToString(),
            AgentId = agentId,
            Title = string.Format(System.Globalization.CultureInfo.CurrentCulture, format, count),
            HasCustomTitle = false,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
    }
}
