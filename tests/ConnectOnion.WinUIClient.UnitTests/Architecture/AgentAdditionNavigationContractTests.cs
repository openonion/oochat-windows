namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class AgentAdditionNavigationContractTests
{
    [Fact]
    public void AddingAnAgent_SelectsItAndNavigatesToItsDetailPage()
    {
        var viewModel = ReadRepositoryFile(
            "ConnectOnion.WinUIClient.Core", "ViewModels", "AddAgentViewModel.cs");
        var window = ReadRepositoryFile("ConnectOnion.WinUIClient", "MainWindow.xaml.cs");
        var callbackStart = window.IndexOf(
            "AddAgentOverlay_AgentAdded(Models.AgentConfig agent)",
            StringComparison.Ordinal);
        Assert.True(callbackStart >= 0);
        var callbackEnd = window.IndexOf(
            "internal System.Threading.Tasks.Task<bool> DeleteAgentAsync",
            callbackStart,
            StringComparison.Ordinal);

        Assert.True(callbackStart >= 0 && callbackEnd > callbackStart);
        var callback = window[callbackStart..callbackEnd];

        Assert.Contains(
            "AppendAgentAsync(agent, makeSelected: true",
            viewModel,
            StringComparison.Ordinal);
        var closeSettings = callback.IndexOf("CloseSettingsOverlay()", StringComparison.Ordinal);
        var navigate = callback.IndexOf(
            "NavigateTo(typeof(AgentDetailPage), forceReload: true)",
            StringComparison.Ordinal);
        Assert.True(closeSettings >= 0 && navigate > closeSettings);
        Assert.Contains(
            "NavigateTo(typeof(AgentDetailPage), forceReload: true)",
            callback,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshAfterAgentAddedAsync", callback, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(path)) return File.ReadAllText(path);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(relativeParts)} from {AppContext.BaseDirectory}.");
    }
}
