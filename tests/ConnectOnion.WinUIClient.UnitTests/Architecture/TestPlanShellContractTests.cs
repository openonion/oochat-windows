namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class TestPlanShellContractTests
{
    [Fact]
    public void BackForwardButtonsTrackFrameState_AndNavigationClosesFind()
    {
        var source = Read("ConnectOnion.WinUIClient", "MainWindow.xaml.cs");

        Assert.Contains("BackButton.IsEnabled = ContentFrame.CanGoBack", source, StringComparison.Ordinal);
        Assert.Contains("ForwardButton.IsEnabled = ContentFrame.CanGoForward", source, StringComparison.Ordinal);
        Assert.Contains("CloseFindOverlay()", source, StringComparison.Ordinal);
        Assert.Contains("ContentFrame.Navigated +=", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DisclosureAnimation_IsBoundedGuardedAndHonoursTheOsPreference()
    {
        var source = Read(
            "ConnectOnion.WinUIClient", "Controls", "Primitives", "DisclosureAnimation.cs");

        Assert.Contains("TimeSpan.FromMilliseconds(140)", source, StringComparison.Ordinal);
        Assert.Contains("new UISettings().AnimationsEnabled", source, StringComparison.Ordinal);
        Assert.Contains("header.IsEnabled = false", source, StringComparison.Ordinal);
        Assert.Contains("content.IsHitTestVisible = false", source, StringComparison.Ordinal);
        Assert.Contains("finally", source, StringComparison.Ordinal);
        Assert.Contains("header.IsEnabled = true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutOverlay_HasTheRequiredProductCopyAndIcon()
    {
        var xaml = Read(
            "ConnectOnion.WinUIClient", "Controls", "Shell", "AboutOverlay.xaml");

        Assert.Contains("ConnectOnion", xaml, StringComparison.Ordinal);
        Assert.Contains("Assets/", xaml, StringComparison.Ordinal);
        Assert.Contains("Copyright", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ScrollViewer", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentMarkdownResponse_ExposesItsTextToUiAutomation()
    {
        var xaml = Read("ConnectOnion.WinUIClient", "Views", "ChatPage.xaml");

        Assert.Contains(
            "AutomationProperties.Name=\"{x:Bind Content, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind IsRegularAgentResponse, Mode=OneWay, Converter={StaticResource BoolToVis}}\"",
            xaml,
            StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
