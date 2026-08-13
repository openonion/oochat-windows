namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class AgentInfoPersistenceContractTests
{
    [Fact]
    public void AgentInfoFetch_UsesTargetedPersistenceInsteadOfAWholeStateSave()
    {
        var source = ReadAppSource("Services", "AgentInfoService.cs");

        Assert.Contains("AppServices.Agents.UpdateInfoAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppServices.Agents.SaveAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppServices.Agents.LoadAsync", source, StringComparison.Ordinal);
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
