namespace ConnectOnion.WinUIClient.UnitTests.Accessibility;

public sealed class OverlayInteractionContractTests
{
    public static TheoryData<string[]> OverlaySources => new()
    {
        new[] { "ConnectOnion.WinUIClient", "Controls", "Settings", "SettingsOverlay.xaml.cs" },
        new[] { "ConnectOnion.WinUIClient", "Controls", "Settings", "KeyboardShortcutsDialog.xaml.cs" },
        new[] { "ConnectOnion.WinUIClient", "Controls", "Shell", "AboutOverlay.xaml.cs" },
        new[] { "ConnectOnion.WinUIClient", "Controls", "Shell", "SessionSearchOverlay.xaml.cs" },
        new[] { "ConnectOnion.WinUIClient", "Controls", "Agents", "AddAgentForm.xaml.cs" },
    };

    [Theory]
    [MemberData(nameof(OverlaySources))]
    public void Overlay_EscapeAndBackdropClose_WhileCardTapStaysInside(string[] path)
    {
        var source = Read(path);

        Assert.Contains("OverlayRoot_KeyDown", source, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.Escape", source, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", source, StringComparison.Ordinal);
        Assert.Contains("Backdrop_Tapped", source, StringComparison.Ordinal);
        Assert.Contains("ModalContainer_Tapped", source, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(OverlaySources))]
    public void Overlay_ReturnsFocusToItsOpenerWhenHidden(string[] path)
    {
        var source = Read(path);

        Assert.Contains("_focusReturnTarget", source, StringComparison.Ordinal);
        Assert.Contains("Focus(FocusState.Programmatic)", source, StringComparison.Ordinal);
        Assert.Contains("_focusReturnTarget = null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_CreatesOneInstancePerOverlay_AndCyclesFocusInsideIt()
    {
        var source = Read("ConnectOnion.WinUIClient", "Shell", "MainWindow.Overlays.cs");

        foreach (var ensure in new[]
                 {
                     "EnsureAddAgentOverlay", "EnsureSettingsOverlay",
                     "EnsureKeyboardShortcutsDialog", "EnsureAboutOverlay",
                     "EnsureSessionSearchOverlay",
                 })
        {
            Assert.Contains(ensure, source, StringComparison.Ordinal);
        }

        Assert.Equal(
            5,
            Count(source, "TabFocusNavigation = KeyboardNavigationMode.Cycle"));
        Assert.Contains("control.IsTabStop = false", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryGlobalAcceleratorHandler_StopsBehindAModalOverlay()
    {
        foreach (var path in new[]
                 {
                     new[] { "ConnectOnion.WinUIClient", "MainWindow.xaml.cs" },
                     new[] { "ConnectOnion.WinUIClient", "Shell", "MainWindow.FileMenu.cs" },
                     new[] { "ConnectOnion.WinUIClient", "Shell", "MainWindow.EditMenu.cs" },
                     new[] { "ConnectOnion.WinUIClient", "Shell", "MainWindow.ViewMenu.cs" },
                     new[] { "ConnectOnion.WinUIClient", "Shell", "MainWindow.HelpMenu.cs" },
                 })
        {
            Assert.Contains("if (IsModalOverlayOpen) return", Read(path), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AddAgent_DisabledActionsFollowTheValidatedViewModelState()
    {
        var xaml = Read(
            "ConnectOnion.WinUIClient", "Controls", "Agents", "AddAgentForm.xaml");

        Assert.Contains(
            "IsEnabled=\"{x:Bind Vm.CanTest, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{x:Bind Vm.CanAdd, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
    }

    private static int Count(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private static string Read(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
