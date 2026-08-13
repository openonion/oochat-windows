namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class AgentRenameContractTests
{
    [Fact]
    public void Rename_IsReachableFromSidebarAndAgentSettings()
    {
        var sidebar = ReadAppSource("Controls", "Shell", "ShellSidebar.xaml");
        var settings = ReadAppSource("Controls", "Settings", "AgentsSettingsContent.xaml");

        Assert.Contains("Click=\"RenameAgent_Click\"", sidebar, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"RenameAgentMenuItem\"", sidebar, StringComparison.Ordinal);
        Assert.Contains("Click=\"RenameAgent_Click\"", settings, StringComparison.Ordinal);
        Assert.Contains("x:Uid=\"AgentSettingsRenameAgent\"", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Rename_UsesTargetedPersistenceAndRefreshesVisibleNameSurfaces()
    {
        var repository = ReadCoreSource("Data", "AgentRepository.cs");
        var host = ReadAppSource("Shell", "MainWindow.Agents.cs");

        Assert.Contains("public async Task<bool> UpdateNameAsync", repository, StringComparison.Ordinal);
        Assert.Contains("UPDATE agents", repository, StringComparison.Ordinal);
        Assert.Contains("WHERE id = $id AND name <> $name", repository, StringComparison.Ordinal);
        Assert.Contains("AppServices.Agents.UpdateNameAsync", host, StringComparison.Ordinal);
        Assert.Contains("ShellSidebar.RefreshAsync()", host, StringComparison.Ordinal);
        Assert.Contains("RefreshAgentsAsync()", host, StringComparison.Ordinal);
        Assert.Contains("RefreshTrayRecentChatsAsync()", host, StringComparison.Ordinal);
        Assert.Contains("RefreshAgentPresentationAsync(agentId)", host, StringComparison.Ordinal);
    }

    private static string ReadAppSource(params string[] relativeParts)
        => ReadSource("ConnectOnion.WinUIClient", relativeParts);

    private static string ReadCoreSource(params string[] relativeParts)
        => ReadSource("ConnectOnion.WinUIClient.Core", relativeParts);

    private static string ReadSource(string project, params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var root = Path.Combine(directory.FullName, project);
            if (Directory.Exists(root))
                return File.ReadAllText(Path.Combine([root, .. relativeParts]));
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate {project} source directory.");
    }
}
