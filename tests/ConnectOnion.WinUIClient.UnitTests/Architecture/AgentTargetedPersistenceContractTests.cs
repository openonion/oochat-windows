namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class AgentTargetedPersistenceContractTests
{
    [Fact]
    public void ProductionAgentMutations_DoNotUseWholeStateSave()
    {
        var appSource = ReadTree("ConnectOnion.WinUIClient");
        var coreViewModels = ReadTree(Path.Combine("ConnectOnion.WinUIClient.Core", "ViewModels"));

        Assert.DoesNotContain("AppServices.Agents.SaveAsync", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_agents.SaveAsync", coreViewModels, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellDeletion_DelegatesPersistedGraphRemovalToOneRepositoryTransaction()
    {
        var source = ReadSource(
            "ConnectOnion.WinUIClient", "Controls", "Shell", "ShellSidebar.Events.cs");

        Assert.Contains("AppServices.Agents.DeleteAgentAsync", source, StringComparison.Ordinal);
        Assert.Contains("AppServices.Sessions.DeleteSessionAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppServices.Conversations.DeleteMessagesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AppServices.Conversations.DeleteExecutionsAndTracesAsync",
            source,
            StringComparison.Ordinal);
    }

    private static string ReadTree(string relativeDirectory)
    {
        var root = FindRoot();
        return string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(Path.Combine(root, relativeDirectory), "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
    }

    private static string ReadSource(params string[] relativeParts)
        => File.ReadAllText(Path.Combine([FindRoot(), .. relativeParts]));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "ConnectOnion.WinUIClient")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
