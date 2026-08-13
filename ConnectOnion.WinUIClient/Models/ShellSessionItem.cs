using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Common;

namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// Lightweight projection for a conversation row in the desktop sidebar.
/// </summary>
public sealed partial class ShellSessionItem : RowStateItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Preview { get; set; } = "";
    public string TimeGroup { get; set; } = "Earlier";
    public string UpdatedAtLabel { get; set; } = "";
    public int UnreadCount { get; set; }
    public bool RequiresAttention { get; set; }
    public bool HasUnread => UnreadCount > 0;
    public string UnreadBadgeText => UnreadCount > 99
        ? "99+"
        : UnreadCount.ToString(CultureInfo.CurrentCulture);
    /// <summary>
    /// Raw owning-agent id, for the surfaces that show which agent a conversation belongs to.
    ///
    /// <para>Only <c>SessionSearchOverlay</c> does — its result rows bind
    /// <see cref="AgentDisplayName"/>, because a search spans every agent and a bare title would be
    /// ambiguous. The sidebar leaves this empty: a conversation there is already under its agent's
    /// row, and populating it meant reading every agent (and every cached <c>/info</c> blob) on the
    /// paged load paths to fill a field no sidebar template draws.</para>
    /// </summary>
    public string AgentName { get; set; } = "";
    public string AgentDisplayName => FriendlyAgentName.From(AgentName);
    public string AccessibilityName
        => string.Join(", ", new[]
            {
                Title,
                RequiresAttention
                    ? LocalizedStrings.Get("SidebarRequiresAttention", "Approval required")
                    : HasUnread
                        ? LocalizedStrings.Format("SidebarUnreadCount", "{0} unread", UnreadCount)
                        : "",
                UpdatedAtLabel,
                Preview,
            }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    public string PinToolTip => IsPinned
        ? LocalizedStrings.Get("SidebarUnpinChat", "Unpin chat")
        : LocalizedStrings.Get("SidebarPinChat", "Pin chat");

    /// <summary>True for the copy of this conversation that appears in the pinned shortcuts
    /// section, false for the copy nested under its owning agent. The two are separate row objects
    /// for the same conversation and draw differently — different indent, different click handler,
    /// different AutomationId — so the flat row list needs to tell them apart. Distinct from
    /// <see cref="IsPinned"/>, which says whether the conversation <i>is</i> pinned and is true on
    /// both copies. Plain settable state rather than an observable: a rebuild creates fresh rows,
    /// and nothing ever moves a row between the two sections in place.</summary>
    public bool IsPinnedRow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinToolTip))]
    public partial bool IsPinned { get; set; }
}
