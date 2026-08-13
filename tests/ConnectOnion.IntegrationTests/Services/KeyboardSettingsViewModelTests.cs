using ConnectOnion.IntegrationTests.Database;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.ViewModels;

namespace ConnectOnion.IntegrationTests.Services;

/// <summary>
/// The editing surface behind Settings → Keyboard. Lives here rather than in the unit tests
/// because a row's whole job is to move a real, persisted binding.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class KeyboardSettingsViewModelTests
{
    private const int VkJ = 'J';
    private const int VkB = 'B';

    private static async Task<(KeyboardSettingsViewModel Vm, KeyboardShortcutService Service)> FreshAsync()
    {
        await using (var connection = await AppDatabase.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM preferences;";
            await command.ExecuteNonQueryAsync();
        }

        var service = new KeyboardShortcutService(new PreferencesRepository());
        await service.LoadAsync();
        return (new KeyboardSettingsViewModel(service), service);
    }

    private static KeyboardShortcutRow Row(KeyboardSettingsViewModel vm, string name)
        => vm.Groups.SelectMany(g => g.Rows).Single(r => r.Name == name);

    [Fact]
    public async Task Constructor_NoSearch_ListsTheWholeCatalogWithBothRowKinds()
    {
        var (vm, _) = await FreshAsync();

        var rows = vm.Groups.SelectMany(g => g.Rows).ToList();
        Assert.Equal(16, rows.Count(r => r.IsCustomizable));
        Assert.Equal(12, rows.Count(r => !r.IsCustomizable));
        Assert.False(vm.IsEmpty);
        Assert.False(vm.HasAnyRebinding);
    }

    [Fact]
    public async Task FixedRow_Always_CarriesAReasonAndNoEditor()
    {
        var (vm, _) = await FreshAsync();

        foreach (var row in vm.Groups.SelectMany(g => g.Rows).Where(r => !r.IsCustomizable))
        {
            Assert.NotEmpty(row.ReadOnlyReason);
            Assert.True(row.Chord.IsEmpty);
            Assert.NotEmpty(row.Binding.DisplayText);   // still shows what the key is
        }
    }

    [Fact]
    public async Task SearchText_ActionName_FiltersLikeTheDialogDoes()
    {
        var (vm, _) = await FreshAsync();

        vm.SearchText = "terminal";

        var group = Assert.Single(vm.Groups);
        Assert.Equal("View", group.Title);
        Assert.Equal("Open terminal", Assert.Single(group.Rows).Name);
    }

    [Fact]
    public async Task SearchText_NoMatch_ShowsEmptyState()
    {
        var (vm, _) = await FreshAsync();

        vm.SearchText = "definitely-not-a-shortcut";

        Assert.Empty(vm.Groups);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task TryRebindAsync_FreeChord_MovesTheLiveBindingAndOffersReset()
    {
        var (vm, service) = await FreshAsync();
        var row = Row(vm, "New chat");

        Assert.True(await row.TryRebindAsync(new KeyChord(true, true, false, VkJ)));

        Assert.Equal("Ctrl + Shift + J", row.Binding.DisplayText);
        Assert.True(row.IsRebound);
        Assert.False(row.HasConflict);
        Assert.Equal(KeyboardShortcutCatalog.Ids.NewChat, service.Match(VkJ, ctrl: true, shift: true, alt: false));
    }

    [Fact]
    public async Task TryRebindAsync_ChordHeldByAnother_ReportsItAndLeavesTheBindingAlone()
    {
        var (vm, service) = await FreshAsync();
        var row = Row(vm, "New chat");

        Assert.False(await row.TryRebindAsync(new KeyChord(true, false, false, VkB)));

        Assert.True(row.HasConflict);
        Assert.Contains("Toggle sidebar", row.Conflict, StringComparison.Ordinal);
        Assert.Equal("Ctrl + N", row.Binding.DisplayText);
        Assert.False(row.IsRebound);
        Assert.Equal(KeyboardShortcutCatalog.Ids.ToggleSidebar, service.Match(VkB, ctrl: true, shift: false, alt: false));
    }

    [Fact]
    public async Task TryRebindAsync_FixedRow_IsRefusedOutright()
    {
        var (vm, _) = await FreshAsync();
        var row = Row(vm, "Copy");

        Assert.False(await row.TryRebindAsync(new KeyChord(true, true, false, VkJ)));
    }

    [Fact]
    public async Task ResetAsync_ReboundRow_RestoresTheDefaultAndHidesReset()
    {
        var (vm, _) = await FreshAsync();
        var row = Row(vm, "New chat");
        await row.TryRebindAsync(new KeyChord(true, true, false, VkJ));

        await row.ResetAsync();

        Assert.Equal("Ctrl + N", row.Binding.DisplayText);
        Assert.False(row.IsRebound);
    }

    [Fact]
    public async Task ResetAllAsync_ManyRebinds_RestoresEveryRow()
    {
        var (vm, _) = await FreshAsync();
        await Row(vm, "New chat").TryRebindAsync(new KeyChord(true, true, false, VkJ));
        await Row(vm, "Find").TryRebindAsync(new KeyChord(true, true, true, 'F'));
        vm.RefreshRebindingState();
        Assert.True(vm.HasAnyRebinding);

        await vm.ResetAllAsync();

        Assert.Equal("Ctrl + N", Row(vm, "New chat").Binding.DisplayText);
        Assert.Equal("Ctrl + F", Row(vm, "Find").Binding.DisplayText);
        Assert.False(vm.HasAnyRebinding);
    }

    /// <summary>A refused capture must not leave its warning on screen once the row moves on.</summary>
    [Fact]
    public async Task RefreshRows_AfterAConflict_ClearsTheWarning()
    {
        var (vm, _) = await FreshAsync();
        var row = Row(vm, "New chat");
        await row.TryRebindAsync(new KeyChord(true, false, false, VkB));
        Assert.True(row.HasConflict);

        vm.RefreshRows();

        Assert.False(row.HasConflict);
    }

    [Fact]
    public async Task Rebind_IsVisibleToTheReadOnlyDialog_SoTheTwoNeverDisagree()
    {
        var (vm, service) = await FreshAsync();
        await Row(vm, "New chat").TryRebindAsync(new KeyChord(true, true, false, VkJ));

        var dialog = new KeyboardShortcutsViewModel(service);

        var shown = dialog.Groups
            .Single(g => g.Title == "File").Shortcuts
            .Single(s => s.Name == "New chat");
        Assert.Equal("Ctrl + Shift + J", shown.KeyBindings.Single().DisplayText);
    }
}
