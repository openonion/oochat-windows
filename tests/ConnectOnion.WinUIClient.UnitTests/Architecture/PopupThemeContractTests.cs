namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class PopupThemeContractTests
{
    [Fact]
    public void ContentDialogs_UseTheSharedThemedShowPath()
    {
        var appRoot = FindAppRoot();
        var offenders = Directory
            .EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(
                "ContentDialogExtensions.cs",
                StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("dialog.ShowAsync(", StringComparison.Ordinal)
                    || source.Contains("confirmation.ShowAsync(", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(appRoot, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ThemedShowPath_AppliesAndTracksTheWindowTheme()
    {
        var appRoot = FindAppRoot();
        var extension = File.ReadAllText(Path.Combine(
            appRoot,
            "Common",
            "ContentDialogExtensions.cs"));
        var themeService = File.ReadAllText(Path.Combine(
            appRoot,
            "Services",
            "ThemeService.cs"));

        Assert.Contains(
            "ApplyTheme(ThemeService.CurrentTheme);",
            extension,
            StringComparison.Ordinal);
        Assert.Contains(
            "ThemeService.ThemeApplied += ApplyTheme;",
            extension,
            StringComparison.Ordinal);
        Assert.Contains(
            "ThemeService.ThemeApplied -= ApplyTheme;",
            extension,
            StringComparison.Ordinal);
        Assert.Contains(
            "public static ElementTheme CurrentTheme => _currentTheme;",
            themeService,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublishTheme(requestedTheme == ElementTheme.Default",
            themeService,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SidebarFooter_IsASingleSettingsRowWithoutAnAccountMenu()
    {
        var appRoot = FindAppRoot();
        var xaml = File.ReadAllText(Path.Combine(
            appRoot,
            "Controls",
            "Shell",
            "ShellSidebar.xaml"));
        var source = File.ReadAllText(Path.Combine(
            appRoot,
            "Controls",
            "Shell",
            "ShellSidebar.Events.cs"));

        Assert.Contains("x:Name=\"SidebarSettingsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SettingsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"BottomSettings_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Icon=\"Settings\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{x:Bind OnlineAgentCountText, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        var settingsStart = xaml.IndexOf("x:Name=\"SidebarSettingsButton\"", StringComparison.Ordinal);
        Assert.True(settingsStart >= 0);
        var settingsEnd = xaml.IndexOf("</Button>", settingsStart, StringComparison.Ordinal);
        Assert.True(settingsEnd > settingsStart);
        Assert.DoesNotContain(
            "OnlineAgentCount",
            xaml[settingsStart..settingsEnd],
            StringComparison.Ordinal);
        Assert.DoesNotContain("BottomAccountButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountMenuPanel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BottomAccount_Click", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseAccountMenu", source, StringComparison.Ordinal);
    }

    private static string FindAppRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var root = Path.Combine(directory.FullName, "ConnectOnion.WinUIClient");
            if (Directory.Exists(root))
                return root;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the WinUI app source directory.");
    }
}
