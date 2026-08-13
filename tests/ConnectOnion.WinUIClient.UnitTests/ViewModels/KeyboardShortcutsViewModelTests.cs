using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.ViewModels;

namespace ConnectOnion.WinUIClient.UnitTests.ViewModels;

public sealed class KeyboardShortcutsViewModelTests
{
    [Fact]
    public void Constructor_NoSearch_ShowsEveryCatalogGroup()
    {
        var viewModel = new KeyboardShortcutsViewModel();

        Assert.Equal(KeyboardShortcutCatalog.GetGroups().Count, viewModel.Groups.Count);
        Assert.False(viewModel.IsEmpty);
    }

    [Fact]
    public void SearchText_ActionName_FiltersToMatchingItem()
    {
        var viewModel = new KeyboardShortcutsViewModel { SearchText = "terminal" };

        var group = Assert.Single(viewModel.Groups);
        Assert.Equal("View", group.Title);
        Assert.Equal("Open terminal", Assert.Single(group.Shortcuts).Name);
    }

    [Fact]
    public void SearchText_KeyCombination_FiltersCaseInsensitively()
    {
        var viewModel = new KeyboardShortcutsViewModel { SearchText = "f11" };

        Assert.Equal("Toggle full screen", Assert.Single(Assert.Single(viewModel.Groups).Shortcuts).Name);
    }

    [Fact]
    public void SearchText_GroupTitleMatch_ShowsWholeGroup()
    {
        var viewModel = new KeyboardShortcutsViewModel { SearchText = "chat" };

        var group = Assert.Single(viewModel.Groups, candidate => candidate.Title == "Chat");
        Assert.Equal("Chat", group.Title);
        // A title match shows the group whole, including the entries that do not match the query
        // themselves — so this count tracks the Chat group in the catalog, not the search.
        Assert.Equal(4, group.Shortcuts.Count);
        Assert.Contains(group.Shortcuts, shortcut => shortcut.Name == "Go to pending decision");
    }

    [Fact]
    public void SearchText_NoMatches_ShowsEmptyState()
    {
        var viewModel = new KeyboardShortcutsViewModel { SearchText = "definitely-not-a-shortcut" };

        Assert.Empty(viewModel.Groups);
        Assert.True(viewModel.IsEmpty);
    }

    [Fact]
    public void Reset_PreviousSearch_RestoresFullCatalog()
    {
        var viewModel = new KeyboardShortcutsViewModel { SearchText = "terminal" };

        viewModel.Reset();

        Assert.Equal("", viewModel.SearchText);
        Assert.Equal(KeyboardShortcutCatalog.GetGroups().Count, viewModel.Groups.Count);
    }
}
