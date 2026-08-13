using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// Invisible sidebar resize target that exposes the standard horizontal-resize cursor while the
/// pointer is over the shell boundary. MainWindow owns the pointer-capture sizing behavior.
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
