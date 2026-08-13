using System;
using System.Runtime.InteropServices;

namespace ConnectOnion.WinUIClient;

// Minimal native window interop retained by MainWindow.Tray.cs. H.NotifyIcon owns the
// notification-area icon, callback window, context menu, and their native resources.
public sealed partial class MainWindow
{
    /// <summary>Subclass procedure used to intercept messages on an existing window without
    /// replacing its window proc outright — the supported way to add behavior to a window the
    /// framework owns, since it chains back through <c>DefSubclassProc</c>.</summary>
    private delegate IntPtr SUBCLASSPROC(
        IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, UIntPtr dwRefData);

    // Comctl32 subclassing rather than SetWindowLongPtr(GWLP_WNDPROC): the window belongs to
    // WinUI, and swapping its window proc outright would break any other component doing the
    // same. Every SetWindowSubclass needs a matching RemoveWindowSubclass at teardown, with the
    // *same* delegate instance and id, or the shell keeps calling into a dead window.
    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd, SUBCLASSPROC pfnSubclass,
        UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd, SUBCLASSPROC pfnSubclass,
        UIntPtr uIdSubclass);

    [DllImport("Comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(
        IntPtr hWnd,
        IntPtr updateRect,
        IntPtr updateRegion,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

}
