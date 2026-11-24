using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// A Invisible resize target for sidebars that shows the standard resize cursor when the user cursor
/// is over the edge of a pane.
/// </summary>
public sealed class SidebarResizeHandle : Control
{
    public SidebarResizeHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}

/// <summary>The template hit-test surface carries the same cursor so child hit testing cannot
/// fall back to the arrow before the routed pointer event reaches the outer control.</summary>
public sealed class SidebarResizeCursorSurface : Grid
{
    public SidebarResizeCursorSurface()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
