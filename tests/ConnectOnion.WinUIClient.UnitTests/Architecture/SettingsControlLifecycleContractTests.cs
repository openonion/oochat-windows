namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class SettingsControlLifecycleContractTests
{
    [Fact]
    public void AgentsSettings_UnloadedDoesNotResolveFromPossiblyDisposedProvider()
    {
        var source = ReadAppSource(
            "Controls",
            "Settings",
            "AgentsSettingsContent.xaml.cs");

        Assert.Contains(
            "private readonly AgentPresenceService _presence = AppServices.Presence;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "=> _presence.PresenceChanged -= OnPresenceChanged;",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "=> AppServices.Presence.PresenceChanged -= OnPresenceChanged;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentsSettings_QueuedPresenceCallbackStopsAfterUnload()
    {
        var source = ReadAppSource(
            "Controls",
            "Settings",
            "AgentsSettingsContent.xaml.cs");

        Assert.Contains("if (!IsLoaded) return;", source, StringComparison.Ordinal);
        Assert.Contains("_presence.GetPresence(agentId)", source, StringComparison.Ordinal);
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
