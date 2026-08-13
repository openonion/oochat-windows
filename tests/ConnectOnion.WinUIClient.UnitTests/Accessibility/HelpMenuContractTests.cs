using System.Text.RegularExpressions;

namespace ConnectOnion.WinUIClient.UnitTests.Accessibility;

public sealed class HelpMenuContractTests
{
    [Fact]
    public void MainWindowXaml_HelpMenu_ContainsRequiredCommandsInOrder()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "ConnectOnion.WinUIClient", "MainWindow.xaml"));
        var shortcuts = xaml.IndexOf("Text=\"Keyboard shortcuts\"", StringComparison.Ordinal);
        var docs = xaml.IndexOf("Text=\"ConnectOnion Docs\"", StringComparison.Ordinal);
        var about = xaml.IndexOf("Text=\"About ConnectOnion\"", StringComparison.Ordinal);

        Assert.True(shortcuts >= 0);
        Assert.True(docs > shortcuts);
        Assert.True(about > docs);
    }

    [Fact]
    public void HelpMenuCode_DocsCommand_UsesCanonicalUrlAndIndependentHandlers()
    {
        var code = File.ReadAllText(FindProjectFile("MainWindow.HelpMenu.cs"));

        Assert.Contains("https://docs.connectonion.com/", code, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"ConnectOnionDocs_Click[\s\S]*?UriLauncher\.LaunchAsync\(new Uri\(DocsUrl\)\)", RegexOptions.CultureInvariant), code);
        Assert.Contains("AboutConnectOnion_Click", code, StringComparison.Ordinal);
        Assert.Contains("ShowAboutOverlay", code, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpMenuCode_FailedDocsLaunchSurfacesAnErrorToast()
    {
        var code = File.ReadAllText(FindProjectFile("MainWindow.HelpMenu.cs"));
        var handlerStart = code.IndexOf(
            "private async void ConnectOnionDocs_Click",
            StringComparison.Ordinal);
        var nextHandler = code.IndexOf(
            "private void AboutConnectOnion_Click",
            handlerStart,
            StringComparison.Ordinal);

        Assert.True(handlerStart >= 0 && nextHandler > handlerStart);
        var handler = code[handlerStart..nextHandler];
        Assert.Contains("if (!launched)", handler, StringComparison.Ordinal);
        Assert.Contains("Couldn't open the docs", handler, StringComparison.Ordinal);
        Assert.Contains("NotificationType.Error", handler, StringComparison.Ordinal);
        Assert.Contains("Visit {DocsUrl} in your browser.", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortcutKeycaps_AreRawAndTheGroupAnnouncesTheWholeChord()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "ConnectOnion.WinUIClient",
            "Controls",
            "Settings",
            "KeyboardShortcutsDialog.xaml"));

        Assert.Contains(
            "AutomationProperties.Name=\"{x:Bind KeysAccessibleText}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.AccessibilityView=\"Raw\"",
            xaml,
            StringComparison.Ordinal);
    }

    /// <summary>Locates a source file inside the app project by name, wherever it currently sits.
    /// Searched rather than addressed by a fixed path so that reorganising the project folders
    /// does not fail a test that is asserting on file <i>contents</i> — the previous hard-coded
    /// path broke the moment the MainWindow partials moved into <c>Shell/</c>. A duplicate name
    /// fails loudly instead of silently picking one.</summary>
    private static string FindProjectFile(string fileName)
    {
        var matches = Directory.GetFiles(
            Path.Combine(FindRepositoryRoot(), "ConnectOnion.WinUIClient"),
            fileName,
            SearchOption.AllDirectories);

        Assert.True(matches.Length == 1,
            $"Expected exactly one '{fileName}' in the app project, found {matches.Length}.");
        return matches[0];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "ConnectOnion.WinUIClient"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
