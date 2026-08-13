using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Common;

namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// The sidebar's rows are one flat list, not a tree of nested <c>ItemsRepeater</c>s, and these are
/// the row kinds that are not already a <see cref="ShellAgentItem"/> or a
/// <see cref="ShellSessionItem"/>.
///
/// <para><b>Why flat.</b> <c>ItemsRepeater</c> virtualizes against the nearest scrolling surface.
/// An inner repeater sits inside its parent's item container, which is measured with an
/// unconstrained height, so the inner one sees an infinite viewport and realizes <i>every</i> item
/// — the sidebar used to build a full visual subtree for every conversation of every expanded
/// agent, however far off screen. The same applied to the outer agent repeater, because a
/// <c>StackPanel</c> wrapped it inside the <c>ScrollViewer</c> and measures its children the same
/// way. One repeater whose items are heterogeneous rows is what gives the whole tree a real
/// viewport, and <see cref="Presentation.ShellSidebarRowTemplateSelector"/> is what lets one
/// repeater draw five different rows.</para>
///
/// <para>Expansion therefore becomes a list edit rather than a <c>Visibility</c> flip: a collapsed
/// agent's conversation rows are simply absent from the flat list. That is also what makes the
/// virtualization real — rows that exist but are collapsed would still be realized.</para>
/// </summary>
public sealed partial class ShellPinnedHeaderRow : Common.ObservableObject
{
    /// <summary>Mirrors the sidebar's own pinned-expansion state. Held on the row because the row
    /// is what the template binds to — a <c>DataTemplate</c> inside the repeater resolves
    /// <c>x:Bind</c> against its item, not against the enclosing control.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronAngle))]
    [NotifyPropertyChangedFor(nameof(AccessibilityName))]
    public partial bool IsExpanded { get; set; }

    public double ChevronAngle => IsExpanded ? 0 : 270;

    public string AccessibilityName => IsExpanded
        ? LocalizedStrings.Get("SidebarCollapsePinnedChats", "Collapse pinned shortcuts")
        : LocalizedStrings.Get("SidebarExpandPinnedChats", "Expand pinned shortcuts");
}

/// <summary>A time-group label ("Today" / "Yesterday" / "Earlier") between an agent's row and the
/// conversations recorded in that bucket. Replaces <see cref="ShellSessionGroup"/>'s role as a
/// container: the grouping still decides the order and where the labels fall, but the label is now
/// a row of its own rather than a nested list with a header.</summary>
public sealed class ShellSessionGroupHeaderRow
{
    public ShellSessionGroupHeaderRow(string label) => Label = label;

    /// <summary>Already localized by <see cref="ShellSessionGroup.Label"/> before it reaches
    /// here — the raw "Today"/"Yesterday"/"Earlier" keys stay internal to the grouping.</summary>
    public string Label { get; }
}

/// <summary>
/// The row that ends an agent's branch when it has conversations older than the page loaded so
/// far. Clicking it pulls the next page in and rebuilds the flat list.
///
/// <para>Carries the agent id rather than the item because the row templates set <c>Tag</c> from
/// it and the sidebar's handlers all take an id — the same shape as every other row's click.</para>
/// </summary>
public sealed partial class ShellShowMoreSessionsRow : Common.ObservableObject
{
    public ShellShowMoreSessionsRow(string agentId) => AgentId = agentId;

    public string AgentId { get; }

    /// <summary>Set while the next page is in flight so the row can say so and stop accepting
    /// repeat clicks. A page is one indexed query, so this is usually invisible — it exists
    /// because a slow disk should not let the user queue five pages with five clicks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    public partial bool IsLoading { get; set; }

    public bool IsEnabled => !IsLoading;

    public string Label => IsLoading
        ? LocalizedStrings.Get("SidebarLoadingMoreChats", "Loading…")
        : LocalizedStrings.Get("SidebarShowMoreChats", "Show more");
}
