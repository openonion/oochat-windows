namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class AgentIconRefreshContractTests
{
    [Fact]
    public void IconMutations_RefreshTheVisibleHomeOrAgentDetailSurface()
    {
        var source = ReadAppSource("Controls", "Shell", "ShellSidebar.Events.cs");

        Assert.Equal(
            2,
            CountOccurrences(source, "RefreshCurrentAgentSurface();"));
        Assert.Contains("_currentPageType == typeof(HomePage)", source, StringComparison.Ordinal);
        Assert.Contains("_currentPageType == typeof(AgentDetailPage)", source, StringComparison.Ordinal);
        Assert.Contains(
            "RequestNavigation(_currentPageType, forceReload: true)",
            source,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
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
