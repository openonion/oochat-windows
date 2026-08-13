namespace ConnectOnion.WinUIClient.UnitTests.Accessibility;

public sealed class TextButtonAffordanceTests
{
    [Fact]
    public void StandaloneTextActions_UseVisibleHoverCapableStyle()
    {
        var root = FindRepositoryRoot();
        var styles = File.ReadAllText(Path.Combine(
            root, "ConnectOnion.WinUIClient", "Styles", "ControlStyles.xaml"));
        var cards = File.ReadAllText(Path.Combine(
            root, "ConnectOnion.WinUIClient", "Styles", "InteractiveCards.xaml"));
        var agentDetail = File.ReadAllText(Path.Combine(
            root, "ConnectOnion.WinUIClient", "Views", "AgentDetailPage.xaml"));
        var tools = File.ReadAllText(Path.Combine(
            root, "ConnectOnion.WinUIClient", "Controls", "Chat", "ToolActivityView.xaml"));
        var window = File.ReadAllText(Path.Combine(
            root, "ConnectOnion.WinUIClient", "MainWindow.xaml"));

        Assert.Contains("x:Key=\"TextActionButtonStyle\"", styles, StringComparison.Ordinal);
        Assert.Contains("Property=\"Background\" Value=\"{ThemeResource SurfaceSecondaryBrush}\"", styles, StringComparison.Ordinal);
        Assert.Contains("Property=\"BorderBrush\" Value=\"{ThemeResource BorderSubtleBrush}\"", styles, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource TextActionButtonStyle}\"", cards, StringComparison.Ordinal);
        Assert.Equal(3, Count(agentDetail, "Style=\"{StaticResource TextActionButtonStyle}\""));
        Assert.Contains("BasedOn=\"{StaticResource TextActionButtonStyle}\"", tools, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ResetZoomButton\"", window, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource TextActionButtonStyle}\"", window, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
        => (source.Length - source.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "ConnectOnion.WinUIClient")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the ConnectOnion repository root.");
    }
}
