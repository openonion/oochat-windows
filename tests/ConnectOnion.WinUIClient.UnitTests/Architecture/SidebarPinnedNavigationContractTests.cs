namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class SidebarPinnedNavigationContractTests
{
    [Fact]
    public void PinnedRows_CarryTheActiveConversationSelection()
    {
        var source = ReadAppSource("Controls", "Shell", "ShellSidebar.xaml.cs");

        Assert.Contains(
            "IsSelected = IsSessionSelected(session.Id, activeSessionId)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("!isPinnedShortcut", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionNavigation_RevealsItsAgentWithoutCollapsingOtherBranches()
    {
        var source = ReadAppSource("Controls", "Shell", "ShellSidebar.xaml.cs");
        var events = ReadAppSource("Controls", "Shell", "ShellSidebar.Events.cs");

        Assert.Contains("RevealAgentInSidebar(agentId)", source, StringComparison.Ordinal);
        Assert.Contains("_expandedAgentIds.Add(agentId)", source, StringComparison.Ordinal);
        Assert.Contains("agent.IsExpanded = true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FocusAgentInSidebar(session.AgentId)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("preserveOtherExpandedAgents", source, StringComparison.Ordinal);
        Assert.DoesNotContain("preserveOtherExpandedAgents", events, StringComparison.Ordinal);
    }

    [Fact]
    public void AllAgentNavigation_RevealsWithoutDestructiveFocus()
    {
        var source = ReadAppSource("Controls", "Shell", "ShellSidebar.xaml.cs");
        var events = ReadAppSource("Controls", "Shell", "ShellSidebar.Events.cs");
        var agentClick = source[source.IndexOf(
            "private async void Agent_Click", StringComparison.Ordinal)..];
        agentClick = agentClick[..agentClick.IndexOf(
            "private void ToggleAgent_Click", StringComparison.Ordinal)];

        Assert.Contains("RevealAgentInSidebar(agentId)", agentClick, StringComparison.Ordinal);
        Assert.Contains("RevealAgentInSidebar(agent.Id)", source, StringComparison.Ordinal);
        Assert.Contains("RevealAgentInSidebar(agentId)", events, StringComparison.Ordinal);
        Assert.DoesNotContain("FocusAgentInSidebar", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FocusAgentInSidebar", events, StringComparison.Ordinal);
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
