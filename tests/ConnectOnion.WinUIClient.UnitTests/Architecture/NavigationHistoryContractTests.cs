namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class NavigationHistoryContractTests
{
    [Fact]
    public void ForcedNavigation_PreservesFrameHistoryForBackAndForwardCommands()
    {
        var source = ReadAppSource("MainWindow.xaml.cs");
        var navigationStart = source.IndexOf(
            "if (forceReload || ContentFrame.CurrentSourcePageType != page)",
            StringComparison.Ordinal);
        var resetStart = source.IndexOf(
            "private void ResetNavigationTo(Type page)",
            navigationStart,
            StringComparison.Ordinal);

        Assert.True(navigationStart >= 0, "The frame navigation branch was not found.");
        Assert.True(resetStart > navigationStart, "The navigation reset boundary was not found.");

        var ordinaryNavigation = source[navigationStart..resetStart];
        Assert.Contains("ContentFrame.Navigate(page, targetContext)", ordinaryNavigation, StringComparison.Ordinal);
        Assert.Contains(
            "ReplaceLatestHistoryEntry(ContentFrame.BackStack, outgoingType, outgoingContext)",
            ordinaryNavigation,
            StringComparison.Ordinal);
        Assert.DoesNotContain("BackStack.Remove", ordinaryNavigation, StringComparison.Ordinal);
        Assert.DoesNotContain("BackStack.Clear", ordinaryNavigation, StringComparison.Ordinal);
        Assert.DoesNotContain("ForwardStack.Clear", ordinaryNavigation, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryCommands_RestoreStoredEntityContextBeforeMovingTheFrame()
    {
        var window = ReadAppSource("MainWindow.xaml.cs");

        Assert.Contains("RestoreNavigationContextAsync(target.SourcePageType, target.Parameter)", window, StringComparison.Ordinal);
        Assert.Contains("var outgoingContext = _currentNavigationContext", window, StringComparison.Ordinal);
        Assert.Contains("_currentNavigationContext = targetContext with { Payload = null }", window, StringComparison.Ordinal);
        Assert.Contains("SetSelectedAgentAsync(session.AgentId)", window, StringComparison.Ordinal);
        Assert.Contains("SetActiveSessionAsync(session.Id)", window, StringComparison.Ordinal);
        Assert.Contains("ReplaceLatestHistoryEntry(ContentFrame.ForwardStack", window, StringComparison.Ordinal);
        Assert.Contains("ReplaceLatestHistoryEntry(ContentFrame.BackStack", window, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDirectEntityNavigation_GoesThroughTheContextCapturingShell()
    {
        var home = ReadAppSource("Views", "HomePage.xaml.cs");
        var agent = ReadAppSource("Views", "AgentDetailPage.xaml.cs");
        var chat = ReadAppSource("Views", "ChatPage.xaml.cs");

        Assert.DoesNotContain("Frame.Navigate(typeof(AgentDetailPage))", home, StringComparison.Ordinal);
        Assert.DoesNotContain("Frame.Navigate(typeof(ChatPage))", home, StringComparison.Ordinal);
        Assert.Contains("MainWindow.FromXamlRoot(XamlRoot)?.NavigateTo", home, StringComparison.Ordinal);
        Assert.Contains("MainWindow.FromXamlRoot(XamlRoot)?.NavigateTo", agent, StringComparison.Ordinal);
        Assert.Contains("e.Parameter is ShellNavigationContext context", chat, StringComparison.Ordinal);
        Assert.Contains("context.Payload", chat, StringComparison.Ordinal);
    }

    [Fact]
    public void DestructiveNavigationReset_StillClearsBothHistoryStacks()
    {
        var source = ReadAppSource("MainWindow.xaml.cs");
        var resetStart = source.IndexOf(
            "private void ResetNavigationTo(Type page)",
            StringComparison.Ordinal);
        Assert.True(resetStart >= 0, "The navigation reset method was not found.");

        var reset = source[resetStart..];
        Assert.Contains("ContentFrame.BackStack.Clear()", reset, StringComparison.Ordinal);
        Assert.Contains("ContentFrame.ForwardStack.Clear()", reset, StringComparison.Ordinal);
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
