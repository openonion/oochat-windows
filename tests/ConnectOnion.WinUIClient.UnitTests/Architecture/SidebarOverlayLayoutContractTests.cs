using System.IO;
using System.Text.RegularExpressions;

namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

/// <summary>
/// Below <see cref="ConnectOnion.WinUIClient.Services.SidebarLayoutPolicy.CompactWindowWidth"/> the
/// sidebar stops being a docked rail and becomes an overlay drawer over the content.
///
/// <para>The drawer keeps <c>Grid.Column="0"</c> while that column is collapsed to zero width, and
/// a <c>Grid</c> arranges a child into its cell rect: a zero-wide slot arranges to zero width, and
/// neither <c>HorizontalAlignment.Left</c> nor an explicit <c>Width</c> makes it overflow. So the
/// drawer took no space and never painted — while the dismiss scrim, which already spanned both
/// columns, did. The bug read as "the toggle only dims the chat page". Spanning both columns is
/// what gives the drawer a full-width slot to be arranged inside.</para>
///
/// <para>This is a source contract because <c>MainWindow</c> lives in the app project, which no
/// headless test host can load. It catches the specific regression — the span going away, or the
/// docked branch forgetting to put it back — not every way this layout could break.</para>
/// </summary>
public sealed class SidebarOverlayLayoutContractTests
{
    [Fact]
    public void CompactSidebar_SpansBothColumnsSoTheDrawerHasWidthToArrangeIn()
    {
        var source = ReadMainWindow();
        var compact = CompactBranch(source);

        Assert.Contains("Grid.SetColumnSpan(ShellSidebar, 2)", compact);
        // The span alone is not the drawer: Left plus the explicit width is what places it.
        Assert.Contains("HorizontalAlignment.Left", compact);
        Assert.Contains("ClampOverlayWidth", compact);
        Assert.Contains("SidebarDismissLayer.Visibility = Visibility.Visible", compact);
    }

    [Fact]
    public void DockedSidebar_RestoresTheSingleColumnSpan()
    {
        // Without this the sidebar would stretch across the content area the moment the window
        // grew back past the breakpoint.
        var source = ReadMainWindow();
        var dockedIndex = source.IndexOf("Grid.SetColumnSpan(ShellSidebar, 1)", StringComparison.Ordinal);
        var compactIndex = source.IndexOf("Grid.SetColumnSpan(ShellSidebar, 2)", StringComparison.Ordinal);

        Assert.True(dockedIndex >= 0, "The docked branch must restore ColumnSpan to 1.");
        Assert.True(compactIndex >= 0, "The compact branch must set ColumnSpan to 2.");
        Assert.True(
            compactIndex < dockedIndex,
            "The compact branch returns early, so it must come first; if the docked reset runs "
            + "before it, the overlay span is overwritten on every layout pass.");
    }

    /// <summary>The body of <c>ApplySidebarWidth</c> from the compact test to its early return.</summary>
    private static string CompactBranch(string source)
    {
        var match = Regex.Match(
            source,
            @"if \(SidebarLayoutPolicy\.IsCompactWindow\(windowWidth\)\)\s*\{(?<body>[\s\S]*?)return;",
            RegexOptions.None);
        Assert.True(match.Success, "Could not locate the compact branch of ApplySidebarWidth.");
        return match.Groups["body"].Value;
    }

    private static string ReadMainWindow()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "ConnectOnion.WinUIClient", "MainWindow.xaml.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate MainWindow.xaml.cs from {AppContext.BaseDirectory}");
    }
}
