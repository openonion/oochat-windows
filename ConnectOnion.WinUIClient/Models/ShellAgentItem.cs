using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// Lightweight projection for the desktop shell's agent -> session navigation.
/// </summary>
public sealed partial class ShellAgentItem : RowStateItem
{
    public string Id { get; set; } = "";
    /// <summary>The raw, stored agent identifier (e.g. <c>remote-admin-agent</c>). Kept
    /// verbatim for tooltips and to drive avatar/lookup logic; the sidebar shows
    /// <see cref="DisplayName"/> instead.</summary>
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string? DirectUrl { get; set; }
    /// <summary>The agent's chosen icon, relative to the data root. Null falls back to
    /// <see cref="Initial"/>.</summary>
    public string? IconPath { get; set; }
    /// <summary>True when this agent has an icon the "remove custom icon" action can remove.</summary>
    public bool HasCustomIcon => !string.IsNullOrWhiteSpace(IconPath);
    /// <summary>User-facing name derived from <see cref="Name"/> (e.g. <c>Remote Admin Agent</c>).
    /// Never persisted — the internal id is unchanged.</summary>
    public string DisplayName => FriendlyAgentName.From(Name);
    public string Initial => NameInitial.FromPair(DisplayName);
    public string ConnectionTarget
        => !string.IsNullOrWhiteSpace(Address) ? Address : DirectUrl ?? "";
    public string CopyAccessibilityName => $"Copy address for {DisplayName}";
    public string DeleteAccessibilityName => $"Delete {DisplayName}";
    public string OpenAccessibilityName => string.Join(", ", new[]
        {
            LocalizedStrings.Format("SidebarOpenAgent", "Open {0}", DisplayName),
            ShowUnreadRollup
                ? RequiresAttention
                    ? LocalizedStrings.Get("SidebarRequiresAttention", "Approval required")
                    : LocalizedStrings.Format("SidebarUnreadCount", "{0} unread", UnreadCount)
                : "",
        }
        .Where(value => !string.IsNullOrWhiteSpace(value)));

    /// <summary>Unread messages summed across every conversation this agent owns — including the
    /// ones the sidebar has not loaded. Read from an aggregate query
    /// (<c>SessionRepository.GetAgentAttentionAsync</c>) rather than from <see cref="Sessions"/>,
    /// which holds only the pages of an <i>expanded</i> branch.</summary>
    public int UnreadCount { get; set; }

    /// <summary>True when one of this agent's conversations is blocked on an approval.</summary>
    public bool RequiresAttention { get; set; }

    /// <summary>
    /// Whether the agent row draws the rolled-up badge.
    ///
    /// <para>Collapsed branches only. An expanded one already shows the badge on the conversation
    /// row that earned it, and a second copy on the parent would read as two separate unread
    /// messages. A conversation cannot hide below an expanded branch's page boundary either:
    /// anything with unread state was just written to, and the branch is ordered by
    /// <c>updated_at DESC</c>, so it is on the first page by construction.</para>
    /// </summary>
    public bool ShowUnreadRollup => !IsExpanded && (UnreadCount > 0 || RequiresAttention);

    public string UnreadBadgeText => UnreadCount > 99
        ? "99+"
        : UnreadCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Hides the count when the branch is only flagged for approval — an approval with no
    /// unread reply would otherwise draw a badge reading "0".</summary>
    public bool ShowUnreadCount => ShowUnreadRollup && UnreadCount > 0;
    public string ToggleAccessibilityName => IsExpanded
        ? LocalizedStrings.Format("SidebarCollapseAgentChats", "Collapse chats for {0}", DisplayName)
        : LocalizedStrings.Format("SidebarExpandAgentChats", "Expand chats for {0}", DisplayName);
    public int SessionCount => Sessions.Count;

    /// <summary>The conversations currently <i>loaded</i> for this agent — one page's worth per
    /// <see cref="LoadedPages"/>, not necessarily all the agent has. See <see cref="HasMoreSessions"/>.</summary>
    public ObservableCollection<ShellSessionItem> Sessions { get; } = new();
    public ObservableCollection<ShellSessionGroup> SessionGroups { get; } = new();

    /// <summary>True when the agent has conversations older than the ones loaded, which is what
    /// puts a "show more" row at the end of its branch.</summary>
    public bool HasMoreSessions { get; set; }

    /// <summary>
    /// Where the next page resumes from — the keyset cursor of the last page loaded.
    ///
    /// <para>Held here rather than derived from the last row because the cursor belongs to the
    /// query, not to the display: <see cref="ShellSessionItem"/> deliberately carries only what a
    /// row draws, and the active-conversation row spliced in by the sidebar is not necessarily the
    /// oldest thing the query returned.</para>
    /// </summary>
    public (string UpdatedAt, string Id)? NextSessionCursor { get; set; }

    /// <summary>
    /// How many pages have been pulled in. A refresh reloads <i>this many</i> rather than resetting
    /// to one — otherwise any incoming message would silently collapse a branch the user had
    /// expanded several pages deep, which reads as the sidebar losing conversations.
    /// </summary>
    public int LoadedPages { get; set; }

    /// <summary>False until this agent's conversations have been fetched at least once. Distinct
    /// from "loaded zero conversations": a collapsed agent is never fetched at all, and that is
    /// the point of paging here — the cost of a branch is paid only when it is opened.</summary>
    public bool SessionsLoaded { get; set; }

    public void RebuildSessionGroups()
    {
        SessionGroups.Clear();
        foreach (var label in new[] { "Today", "Yesterday", "Earlier" })
        {
            var sessions = Sessions.Where(session => session.TimeGroup == label).ToList();
            if (sessions.Count > 0)
            {
                SessionGroups.Add(new ShellSessionGroup(label, sessions));
            }
        }
    }

    public double ChevronAngle => IsExpanded ? 0 : 270;
    public bool IsOnline => Presence == AgentPresence.Online;
    /// <summary>A reachability probe is in flight — shown as a breathing neutral dot.</summary>
    public bool IsChecking => Presence == AgentPresence.Checking;
    /// <summary>Offline or never-probed — shown as a hollow gray dot.</summary>
    public bool IsOfflineIconVisible => Presence == AgentPresence.Offline || Presence == AgentPresence.Unknown;
    public string PresenceLabel => Presence switch
    {
        AgentPresence.Online => Common.LocalizedStrings.Get("PresenceOnline", "Online"),
        AgentPresence.Checking => Common.LocalizedStrings.Get("PresenceConnecting", "Connecting"),
        AgentPresence.Offline => Common.LocalizedStrings.Get("PresenceOffline", "Offline"),
        _ => Common.LocalizedStrings.Get("PresenceNotConnected", "Not connected"),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronAngle))]
    [NotifyPropertyChangedFor(nameof(ToggleAccessibilityName))]
    [NotifyPropertyChangedFor(nameof(ShowUnreadRollup))]
    [NotifyPropertyChangedFor(nameof(ShowUnreadCount))]
    [NotifyPropertyChangedFor(nameof(OpenAccessibilityName))]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline))]
    [NotifyPropertyChangedFor(nameof(IsChecking))]
    [NotifyPropertyChangedFor(nameof(IsOfflineIconVisible))]
    [NotifyPropertyChangedFor(nameof(PresenceLabel))]
    public partial AgentPresence Presence { get; set; }
}

public sealed class ShellSessionGroup
{
    public ShellSessionGroup(string label, IReadOnlyList<ShellSessionItem> sessions)
    {
        _label = label;
        foreach (var session in sessions)
        {
            Sessions.Add(session);
        }
    }

    public string Label => _label switch
    {
        "Today" => LocalizedStrings.Get("TimeGroupToday", "Today"),
        "Yesterday" => LocalizedStrings.Get("TimeGroupYesterday", "Yesterday"),
        _ => LocalizedStrings.Get("TimeGroupEarlier", "Earlier"),
    };
    private readonly string _label;
    public ObservableCollection<ShellSessionItem> Sessions { get; } = new();
}
