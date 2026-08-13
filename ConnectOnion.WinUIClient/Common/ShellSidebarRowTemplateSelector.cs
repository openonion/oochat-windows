using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Presentation;

/// <summary>
/// Draws the sidebar's five row kinds from one <c>ItemsRepeater</c>.
///
/// <para>The tree used to be three nested repeaters, one level per tier, which read naturally but
/// meant nothing in the sidebar virtualized — see <see cref="ShellPinnedHeaderRow"/> for why. The
/// nesting is now flattened into a single list of heterogeneous rows, and this is what lets one
/// repeater still render an agent header differently from a conversation. <c>DataTemplateSelector</c>
/// implements <c>IElementFactory</c>, which is the type <c>ItemsRepeater.ItemTemplate</c> takes.</para>
///
/// <para>Dispatch is by row <i>type</i> on purpose: it keeps every existing template's
/// <c>x:DataType</c> and every <c>x:Bind</c> inside it exactly as it was, so flattening the tree
/// changed the list's shape without touching a single binding. The one case type alone cannot
/// separate is a conversation, which appears both as a pinned shortcut and nested under its agent —
/// same class, two different rows — hence <see cref="ShellSessionItem.IsPinnedRow"/>.</para>
///
/// <para>Resolving once per container is safe here for the same reason it is in
/// <see cref="ChatMessageTemplateSelector"/>: a row's kind is fixed when the flat list is built and
/// never changes underneath it. Expanding an agent rebuilds the list rather than mutating a row.</para>
/// </summary>
public sealed partial class ShellSidebarRowTemplateSelector : DataTemplateSelector
{
    /// <summary>The "Pinned shortcuts" disclosure button.</summary>
    public DataTemplate? PinnedHeader { get; set; }

    /// <summary>A conversation listed in the pinned shortcuts section.</summary>
    public DataTemplate? PinnedSession { get; set; }

    /// <summary>An agent's own row: avatar, name, presence dot, new-chat action.</summary>
    public DataTemplate? Agent { get; set; }

    /// <summary>A "Today" / "Yesterday" / "Earlier" label inside an expanded agent.</summary>
    public DataTemplate? GroupHeader { get; set; }

    /// <summary>A conversation nested under its owning agent.</summary>
    public DataTemplate? Session { get; set; }

    /// <summary>Ends an agent's branch when older conversations remain unloaded.</summary>
    public DataTemplate? ShowMore { get; set; }

    /// <summary>Renders nothing. Unreachable with the rows the sidebar builds today, but a
    /// repeater handed an unrecognized item throws rather than skipping it, and an empty row is a
    /// better failure than a crashed shell.</summary>
    public DataTemplate? Empty { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        ShellPinnedHeaderRow => PinnedHeader,
        ShellAgentItem => Agent,
        ShellSessionGroupHeaderRow => GroupHeader,
        ShellShowMoreSessionsRow => ShowMore,
        // Order matters against the bare ShellSessionItem arm below it.
        ShellSessionItem { IsPinnedRow: true } => PinnedSession,
        ShellSessionItem => Session,
        _ => Empty,
    };
}
