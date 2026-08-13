namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Platform-free sizing rules for the shell sidebar. Keeping the thresholds here makes the
/// drag, resize, and compact-window behavior testable without loading WinUI.
/// </summary>
public static class SidebarLayoutPolicy
{
    public const double DefaultWidth = 288;
    public const double MinimumWidth = 232;
    public const double MaximumWidth = 400;
    public const double CollapseThreshold = 216;
    public const double CompactWindowWidth = 720;
    public const double MinimumContentWidth = 432;
    public const double OverlayMargin = 48;

    public static bool IsCompactWindow(double windowWidth)
        => windowWidth > 0 && windowWidth < CompactWindowWidth;

    public static bool ShouldCollapseFromDrag(double requestedWidth)
        => requestedWidth < CollapseThreshold;

    public static double ClampDockedWidth(double requestedWidth, double windowWidth)
    {
        requestedWidth = NormalizeRequestedWidth(requestedWidth);
        windowWidth = double.IsFinite(windowWidth) ? Math.Max(0, windowWidth) : 0;

        var availableWidth = windowWidth - MinimumContentWidth;
        var effectiveMaximum = Math.Clamp(availableWidth, MinimumWidth, MaximumWidth);
        return Math.Clamp(requestedWidth, MinimumWidth, effectiveMaximum);
    }

    public static double ClampOverlayWidth(double requestedWidth, double windowWidth)
    {
        requestedWidth = NormalizeRequestedWidth(requestedWidth);
        windowWidth = double.IsFinite(windowWidth) ? Math.Max(0, windowWidth) : 0;

        var availableWidth = windowWidth - OverlayMargin;
        var effectiveMaximum = Math.Clamp(availableWidth, MinimumWidth, MaximumWidth);
        return Math.Clamp(requestedWidth, MinimumWidth, effectiveMaximum);
    }

    private static double NormalizeRequestedWidth(double requestedWidth)
        => double.IsFinite(requestedWidth) ? requestedWidth : DefaultWidth;
}
