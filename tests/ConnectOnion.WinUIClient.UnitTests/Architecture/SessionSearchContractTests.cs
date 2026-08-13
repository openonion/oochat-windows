namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class SessionSearchContractTests
{
    [Fact]
    public void SidebarNavigationModule_SeparatesAgentsAndConversationSearchFromBrand()
    {
        var xaml = ReadAppSource("Controls", "Shell", "ShellSidebar.xaml");

        Assert.Contains("x:Name=\"SessionSearchButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SessionSearchButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SearchSessions_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Icon=\"Search\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Uid=\"SidebarConversations\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentsNavigationButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ChatHome_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Icon=\"Bot\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"NewChatButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"ConnectOnion home\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchSelection_ReusesTheShellConversationNavigationPath()
    {
        var source = ReadAppSource("Shell", "MainWindow.SessionSearch.cs");

        Assert.Contains("await ShowConversationAsync(e.AgentId, e.SessionId)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveSessionId =", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchOverlay_UsesLiveFilteringAndKeyboardActivation()
    {
        var xaml = ReadAppSource("Controls", "Shell", "SessionSearchOverlay.xaml");
        var source = ReadAppSource("Controls", "Shell", "SessionSearchOverlay.xaml.cs");

        Assert.Contains("TextChanged=\"SearchBox_TextChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Vm.SearchText = SearchBox.Text", source, StringComparison.Ordinal);
        Assert.Contains("case VirtualKey.Enter:", source, StringComparison.Ordinal);
        Assert.Contains("case VirtualKey.Down:", source, StringComparison.Ordinal);
        Assert.Contains("case VirtualKey.Up:", source, StringComparison.Ordinal);
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
