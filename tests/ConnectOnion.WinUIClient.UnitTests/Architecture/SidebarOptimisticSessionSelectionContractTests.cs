namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class SidebarOptimisticSessionSelectionContractTests
{
    [Fact]
    public void SessionClick_PaintsExistingRowsBeforePersistenceAndNavigation()
    {
        var method = ReadSelectSessionMethod();

        var optimisticPaint = method.IndexOf(
            "ApplyOptimisticSessionSelection(sessionId)",
            StringComparison.Ordinal);
        var persistence = method.IndexOf(
            "QueueSessionSelectionPersistenceAsync(",
            StringComparison.Ordinal);
        var navigation = method.IndexOf(
            "RequestNavigation(typeof(ChatPage), forceReload: true)",
            StringComparison.Ordinal);

        Assert.True(optimisticPaint >= 0, "A session click must paint its row immediately.");
        Assert.True(persistence > optimisticPaint, "Persistence must not delay the selected state.");
        Assert.True(navigation > persistence, "Navigation must use the persisted selection.");
    }

    [Fact]
    public void SessionClick_UsesRenderedOwnerAndKeepsSqliteOffTheUiThread()
    {
        var source = ReadSidebarSource();
        var method = ReadSelectSessionMethod(source);

        Assert.DoesNotContain("AppServices.Sessions.LoadAsync()", method, StringComparison.Ordinal);
        Assert.Contains("ShellAgents", method, StringComparison.Ordinal);

        var persistenceStart = source.IndexOf(
            "private System.Threading.Tasks.Task<bool> PersistSessionSelectionAfterAsync",
            StringComparison.Ordinal);
        var persistenceEnd = source.IndexOf(
            "private ShellAgentItem? FindShellAgent",
            persistenceStart,
            StringComparison.Ordinal);
        var persistenceMethod = source[persistenceStart..persistenceEnd];

        Assert.Contains("System.Threading.Tasks.TaskScheduler.Default", persistenceMethod, StringComparison.Ordinal);
        Assert.Contains("SetActiveSessionAsync(sessionId)", persistenceMethod, StringComparison.Ordinal);
        Assert.Contains("SetSelectedAgentAsync(agentId)", persistenceMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void RapidClicks_AreSerializedAndOnlyLatestRequestNavigates()
    {
        var source = ReadSidebarSource();
        var method = ReadSelectSessionMethod(source);

        Assert.Contains("Interlocked.Increment(ref _sessionSelectionGeneration)", method, StringComparison.Ordinal);
        Assert.Contains("QueueSessionSelectionPersistenceAsync", method, StringComparison.Ordinal);
        Assert.Contains("lock (_sessionSelectionQueueGate)", source, StringComparison.Ordinal);
        Assert.Contains("IsCurrentSessionSelection(selectionGeneration)", method, StringComparison.Ordinal);
    }

    private static string ReadSelectSessionMethod()
        => ReadSelectSessionMethod(ReadSidebarSource());

    private static string ReadSelectSessionMethod(string source)
    {
        var methodStart = source.IndexOf(
            "private async System.Threading.Tasks.Task SelectSessionAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private void ApplyOptimisticSessionSelection",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        return source[methodStart..methodEnd];
    }

    private static string ReadSidebarSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(
                directory.FullName,
                "ConnectOnion.WinUIClient",
                "Controls",
                "Shell",
                "ShellSidebar.xaml.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate ShellSidebar.xaml.cs.");
    }
}
