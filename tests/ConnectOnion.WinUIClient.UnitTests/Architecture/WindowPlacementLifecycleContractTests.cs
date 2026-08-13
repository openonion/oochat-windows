namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class WindowPlacementLifecycleContractTests
{
    [Fact]
    public void SavePath_DoesNotResolveServiceFromPossiblyDisposedProvider()
    {
        var source = ReadAppSource("Shell", "MainWindow.Placement.cs");

        Assert.DoesNotContain("AppServices.WindowPlacement", source, StringComparison.Ordinal);
        Assert.Contains("_windowPlacementStore.SaveAsync", source, StringComparison.Ordinal);
        Assert.Contains("_windowPlacementSaved", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitExit_SavesPlacementBeforeHostShutdown()
    {
        var source = ReadAppSource("Shell", "MainWindow.Tray.cs");
        var save = source.IndexOf("await SaveWindowPlacementAsync()", StringComparison.Ordinal);
        var shutdown = source.IndexOf("await ((App)Application.Current).ShutdownAsync()", StringComparison.Ordinal);

        Assert.True(save >= 0, "Explicit exit does not save window placement.");
        Assert.True(shutdown > save, "Host shutdown must happen after window placement is saved.");
    }

    [Fact]
    public void Placement_RestoresAndPersistsMaximizedState()
    {
        var source = ReadAppSource("Shell", "MainWindow.Placement.cs");

        Assert.Contains("presenter.Maximize()", source, StringComparison.Ordinal);
        Assert.Contains("OverlappedPresenterState.Maximized", source, StringComparison.Ordinal);
        Assert.Contains("new WindowPlacement(position, isMaximized,", source, StringComparison.Ordinal);
    }

    /// <summary>Notification clicks and redirected second launches both bring the existing window
    /// forward. <c>SW_RESTORE</c> must only be used for a genuinely minimized window: applying it
    /// to an already-maximized window cancels maximization as a side effect.</summary>
    [Fact]
    public void BringToForeground_PreservesMaximizedState()
    {
        var source = ReadAppSource("Shell", "MainWindow.Tray.cs");

        Assert.Contains(
            "presenter?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_lastNonMinimizedWasMaximized ? SwShowMaximized : SwRestore",
            source,
            StringComparison.Ordinal);
        Assert.Contains("ShowWindow(_hwnd, SwShow);", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ShowWindow(_hwnd, SwRestore);\r\n        ShowWindow(_hwnd, SwShow);",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>The window's size is restored as well as its position. It used to be neither
    /// saved nor applied — <c>WindowPlacement</c> carried only a position and the restore path
    /// called <c>Move</c> and never <c>Resize</c> — so a resized window reopened at the default
    /// size on every launch.</summary>
    [Fact]
    public void Placement_RestoresAndPersistsWindowSize()
    {
        var source = ReadAppSource("Shell", "MainWindow.Placement.cs");

        Assert.Contains("_appWindow.Resize(", source, StringComparison.Ordinal);
        Assert.Contains("args.DidSizeChange", source, StringComparison.Ordinal);
        Assert.Contains("_lastNormalSize", source, StringComparison.Ordinal);

        // Resize has to precede Move: the work-area clamp measures against the window's size.
        var resize = source.IndexOf("_appWindow.Resize(", StringComparison.Ordinal);
        var move = source.IndexOf("_appWindow.Move(", StringComparison.Ordinal);
        Assert.True(resize < move, "Restore must resize before clamping and moving.");
    }

    /// <summary>Without a presenter minimum the window can be dragged to a few pixels wide, well
    /// past the point where the composer and the overlay sidebar still fit.</summary>
    [Fact]
    public void Placement_AppliesAMinimumWindowSize()
    {
        var source = ReadAppSource("Shell", "MainWindow.Placement.cs");

        Assert.Contains("PreferredMinimumWidth", source, StringComparison.Ordinal);
        Assert.Contains("PreferredMinimumHeight", source, StringComparison.Ordinal);
        Assert.Contains("WindowPlacementPolicy.MinimumWidth", source, StringComparison.Ordinal);

        // The presenter takes device pixels; an unscaled floor is only 400 epx at 160%.
        Assert.Contains("GetDisplayScale()", source, StringComparison.Ordinal);
    }

    private static string ReadAppSource(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var root = Path.Combine(directory.FullName, "ConnectOnion.WinUIClient");
            if (Directory.Exists(root))
                return File.ReadAllText(Path.Combine([root, .. relativeParts]));
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the WinUI app source directory.");
    }
}
