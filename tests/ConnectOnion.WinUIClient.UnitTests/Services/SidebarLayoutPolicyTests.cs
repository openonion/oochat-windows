using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class SidebarLayoutPolicyTests
{
    [Theory]
    [InlineData(215.9, true)]
    [InlineData(216, false)]
    [InlineData(288, false)]
    public void ShouldCollapseFromDrag_UsesAThresholdBelowTheVisibleMinimum(
        double requestedWidth,
        bool expected)
        => Assert.Equal(expected, SidebarLayoutPolicy.ShouldCollapseFromDrag(requestedWidth));

    [Theory]
    [InlineData(100, 1200, 232)]
    [InlineData(288, 1200, 288)]
    [InlineData(500, 1200, 400)]
    [InlineData(400, 760, 328)]
    public void ClampDockedWidth_PreservesSidebarAndContentBounds(
        double requestedWidth,
        double windowWidth,
        double expected)
        => Assert.Equal(expected, SidebarLayoutPolicy.ClampDockedWidth(requestedWidth, windowWidth));

    [Theory]
    [InlineData(719, true)]
    [InlineData(720, false)]
    [InlineData(1200, false)]
    public void IsCompactWindow_UsesTheShellBreakpoint(double windowWidth, bool expected)
        => Assert.Equal(expected, SidebarLayoutPolicy.IsCompactWindow(windowWidth));

    [Fact]
    public void ClampOverlayWidth_LeavesTheDismissMarginReachable()
        => Assert.Equal(272, SidebarLayoutPolicy.ClampOverlayWidth(400, 320));
}
