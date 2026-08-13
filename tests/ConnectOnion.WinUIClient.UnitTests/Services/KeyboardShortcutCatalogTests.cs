using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class KeyboardShortcutCatalogTests
{
    [Fact]
    public void GetGroups_Catalog_HasUniqueActionsAndNonEmptyBindings()
    {
        var items = KeyboardShortcutCatalog.GetGroups().SelectMany(group => group.Shortcuts).ToList();

        Assert.Equal(items.Count, items.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(items, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.NotEmpty(item.KeyBindings);
            Assert.All(item.KeyBindings, binding => Assert.NotEmpty(binding.Keys));
        });
    }

    [Fact]
    public void GetGroups_MenuShortcuts_MatchMainWindowAcceleratorLabels()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "ConnectOnion.WinUIClient", "MainWindow.xaml"));
        var menuGroups = new HashSet<string>(StringComparer.Ordinal) { "File", "Edit", "View" };
        var menuItems = KeyboardShortcutCatalog.GetGroups()
            .Where(group => menuGroups.Contains(group.Title))
            .SelectMany(group => group.Shortcuts)
            .Append(KeyboardShortcutCatalog.GetGroups()
                .Single(group => group.Title == "General").Shortcuts
                .Single(item => item.Name == "Keyboard shortcuts"));

        foreach (var item in menuItems)
        {
            foreach (var binding in item.KeyBindings)
            {
                var compact = string.Join("+", binding.Keys);
                Assert.Contains($"KeyboardAcceleratorTextOverride=\"{compact}\"", xaml, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void GetGroups_CycleChatMode_IsCustomizableAndDefaultsToCtrlShiftM()
    {
        var item = KeyboardShortcutCatalog.GetCustomizable()
            .Single(candidate => candidate.Id == KeyboardShortcutCatalog.Ids.CycleChatMode);

        Assert.Equal("Cycle approval mode", item.Name);
        Assert.Equal("Ctrl+Shift+M", item.DefaultChord.Canonical);
    }

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
