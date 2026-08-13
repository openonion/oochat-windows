using ConnectOnion.IntegrationTests.Database;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.IntegrationTests.Services;

/// <summary>
/// Exercises the resolver against a real preferences row, because the behaviour worth locking in
/// is the round trip: what the key handlers dispatch on after an override has survived SQLite.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class KeyboardShortcutServiceTests
{
    private const int VkN = 'N';
    private const int VkB = 'B';
    private const int VkF = 'F';
    private const int VkJ = 'J';
    private const int VkEqual = 187;
    private const int VkMinus = 189;
    private const int VkNumpadAdd = 107;
    private const int VkNumpadSubtract = 109;
    private const int VkF11 = 122;

    private static readonly KeyChord CtrlN = new(Ctrl: true, Shift: false, Alt: false, VkN);

    private static async Task<KeyboardShortcutService> FreshAsync()
    {
        await ClearPreferencesAsync();
        var service = new KeyboardShortcutService(new PreferencesRepository());
        await service.LoadAsync();
        return service;
    }

    [Fact]
    public async Task Match_NoOverrides_ResolvesEveryCatalogDefault()
    {
        var service = await FreshAsync();

        Assert.Equal(KeyboardShortcutCatalog.Ids.NewChat, service.Match(VkN, ctrl: true, shift: false, alt: false));
        Assert.Equal(KeyboardShortcutCatalog.Ids.ToggleSidebar, service.Match(VkB, ctrl: true, shift: false, alt: false));
        Assert.Equal(KeyboardShortcutCatalog.Ids.Find, service.Match(VkF, ctrl: true, shift: false, alt: false));
        Assert.Equal(KeyboardShortcutCatalog.Ids.ZoomIn, service.Match(VkEqual, ctrl: true, shift: true, alt: false));
        Assert.Equal(KeyboardShortcutCatalog.Ids.ToggleFullScreen, service.Match(VkF11, ctrl: false, shift: false, alt: false));
    }

    [Fact]
    public async Task Match_WrongModifiers_DoesNotFire()
    {
        var service = await FreshAsync();

        Assert.Null(service.Match(VkN, ctrl: false, shift: false, alt: false));
        Assert.Null(service.Match(VkN, ctrl: true, shift: true, alt: false));
        Assert.Null(service.Match(VkN, ctrl: true, shift: false, alt: true));
    }

    /// <summary>Zoom accepted numpad +/- before the resolver existed; that has to still hold.</summary>
    [Fact]
    public async Task Match_NumpadZoomKeys_StillResolveToZoom()
    {
        var service = await FreshAsync();

        Assert.Equal(KeyboardShortcutCatalog.Ids.ZoomIn, service.Match(VkNumpadAdd, ctrl: true, shift: true, alt: false));
        Assert.Equal(KeyboardShortcutCatalog.Ids.ZoomOut, service.Match(VkNumpadSubtract, ctrl: true, shift: false, alt: false));
    }

    [Fact]
    public async Task RebindAsync_FreeChord_TakesEffectAndReleasesTheOldOne()
    {
        var service = await FreshAsync();

        var result = await service.RebindAsync(
            KeyboardShortcutCatalog.Ids.NewChat, new KeyChord(true, true, false, VkJ));

        Assert.True(result.Succeeded);
        Assert.Equal(KeyboardShortcutCatalog.Ids.NewChat, service.Match(VkJ, ctrl: true, shift: true, alt: false));
        Assert.Null(service.Match(VkN, ctrl: true, shift: false, alt: false));
        Assert.True(service.IsRebound(KeyboardShortcutCatalog.Ids.NewChat));
    }

    [Fact]
    public async Task RebindAsync_ChordHeldByAnotherAction_IsRefusedAndNamesTheHolder()
    {
        var service = await FreshAsync();

        var result = await service.RebindAsync(KeyboardShortcutCatalog.Ids.NewChat, new KeyChord(true, false, false, VkB));

        Assert.Equal(RebindOutcome.Conflict, result.Outcome);
        Assert.Equal("Toggle sidebar", result.ConflictingActionName);
        // Both actions keep their original chords — a refused rebind changes nothing.
        Assert.Equal(KeyboardShortcutCatalog.Ids.ToggleSidebar, service.Match(VkB, ctrl: true, shift: false, alt: false));
        Assert.Equal(KeyboardShortcutCatalog.Ids.NewChat, service.Match(VkN, ctrl: true, shift: false, alt: false));
    }

    [Fact]
    public async Task RebindAsync_SameChordItAlreadyHas_IsNotAConflictWithItself()
    {
        var service = await FreshAsync();

        Assert.True((await service.RebindAsync(KeyboardShortcutCatalog.Ids.NewChat, CtrlN)).Succeeded);
    }

    [Fact]
    public async Task RebindAsync_EmptyChordOrUnknownAction_IsInvalid()
    {
        var service = await FreshAsync();

        Assert.Equal(RebindOutcome.Invalid,
            (await service.RebindAsync(KeyboardShortcutCatalog.Ids.NewChat, KeyChord.None)).Outcome);
        Assert.Equal(RebindOutcome.Invalid,
            (await service.RebindAsync("not.a.real.action", CtrlN)).Outcome);
    }

    [Fact]
    public async Task RebindAsync_Override_SurvivesAReload()
    {
        var service = await FreshAsync();
        await service.RebindAsync(KeyboardShortcutCatalog.Ids.NewChat, new KeyChord(true, true, false, VkJ));

        var reloaded = new KeyboardShortcutService(new PreferencesRepository());
        await reloaded.LoadAsync();

        Assert.Equal(KeyboardShortcutCatalog.Ids.NewChat, reloaded.Match(VkJ, ctrl: true, shift: true, alt: false));
    }

    /// <summary>Rebinding back to the factory chord should drop the row, not pin it — otherwise a
    /// user who "reset by hand" would never pick up a future change of default.</summary>
    [Fact]
    public async Task RebindAsync_BackToTheDefault_StoresNoOverrideRow()
    {
        var service = await FreshAsync();
        await service.RebindAsync(KeyboardShortcutCatalog.Ids.NewChat, new KeyChord(true, true, false, VkJ));

        await service.RebindAsync(KeyboardShortcutCatalog.Ids.NewChat, CtrlN);

        Assert.False(service.IsRebound(KeyboardShortcutCatalog.Ids.NewChat));
        var stored = await new PreferencesRepository().LoadAsync();
        Assert.DoesNotContain(KeyboardShortcutCatalog.Ids.NewChat, stored.ShortcutOverrides.Keys);
    }

    [Fact]
    public async Task ResetAsync_ReboundAction_RestoresTheDefault()
    {
        var service = await FreshAsync();
        await service.RebindAsync(KeyboardShortcutCatalog.Ids.NewChat, new KeyChord(true, true, false, VkJ));

        await service.ResetAsync(KeyboardShortcutCatalog.Ids.NewChat);

        Assert.Equal(KeyboardShortcutCatalog.Ids.NewChat, service.Match(VkN, ctrl: true, shift: false, alt: false));
        Assert.Null(service.Match(VkJ, ctrl: true, shift: true, alt: false));
        Assert.False(service.IsRebound(KeyboardShortcutCatalog.Ids.NewChat));
    }

    [Fact]
    public async Task ResetAllAsync_ManyRebinds_RestoresEveryDefault()
    {
        var service = await FreshAsync();
        await service.RebindAsync(KeyboardShortcutCatalog.Ids.NewChat, new KeyChord(true, true, false, VkJ));
        await service.RebindAsync(KeyboardShortcutCatalog.Ids.Find, new KeyChord(true, true, true, VkF));

        await service.ResetAllAsync();

        Assert.Equal(KeyboardShortcutCatalog.Ids.NewChat, service.Match(VkN, ctrl: true, shift: false, alt: false));
        Assert.Equal(KeyboardShortcutCatalog.Ids.Find, service.Match(VkF, ctrl: true, shift: false, alt: false));
    }

    /// <summary>The overrides dictionary is shared with the composer's Enter-key preference. A
    /// rebind must not evict its neighbour.</summary>
    [Fact]
    public async Task RebindAndReset_ComposerEnterKeyPreference_IsLeftIntact()
    {
        await ClearPreferencesAsync();
        var repository = new PreferencesRepository();
        await repository.SaveAsync(new PreferencesSnapshot { EnterToSend = false });

        var service = new KeyboardShortcutService(repository);
        await service.LoadAsync();
        await service.RebindAsync(KeyboardShortcutCatalog.Ids.NewChat, new KeyChord(true, true, false, VkJ));
        Assert.False((await repository.LoadAsync()).EnterToSend);

        await service.ResetAllAsync();

        Assert.False((await repository.LoadAsync()).EnterToSend);
    }

    [Fact]
    public async Task LoadAsync_UnparseableOverride_FallsBackToTheDefault()
    {
        await ClearPreferencesAsync();
        var repository = new PreferencesRepository();
        await repository.SaveAsync(new PreferencesSnapshot
        {
            ShortcutOverrides = new Dictionary<string, string>
            {
                [KeyboardShortcutCatalog.Ids.NewChat] = "Ctrl+ThisKeyDoesNotExist",
            },
        });

        var service = new KeyboardShortcutService(repository);
        await service.LoadAsync();

        Assert.Equal(KeyboardShortcutCatalog.Ids.NewChat, service.Match(VkN, ctrl: true, shift: false, alt: false));
    }

    /// <summary>Two stored overrides can collide (hand-edited rows, or a default that moved under
    /// an existing override). Resolution must be deterministic and must never leave an action with
    /// no chord at all.</summary>
    [Fact]
    public async Task LoadAsync_TwoOverridesOntoOneChord_ResolvesDeterministicallyAndLeavesNobodyDead()
    {
        await ClearPreferencesAsync();
        var repository = new PreferencesRepository();
        await repository.SaveAsync(new PreferencesSnapshot
        {
            ShortcutOverrides = new Dictionary<string, string>
            {
                [KeyboardShortcutCatalog.Ids.NewChat] = "Ctrl+Shift+J",
                [KeyboardShortcutCatalog.Ids.Find] = "Ctrl+Shift+J",
            },
        });

        var service = new KeyboardShortcutService(repository);
        await service.LoadAsync();

        // Catalog order decides the winner; the loser reverts to its default rather than vanishing.
        Assert.Equal(KeyboardShortcutCatalog.Ids.NewChat, service.Match(VkJ, ctrl: true, shift: true, alt: false));
        Assert.Equal(KeyboardShortcutCatalog.Ids.Find, service.Match(VkF, ctrl: true, shift: false, alt: false));
        Assert.False(service.GetChord(KeyboardShortcutCatalog.Ids.Find).IsEmpty);
    }

    [Fact]
    public async Task GetLiveGroups_ReboundAction_ShowsTheNewChordAndKeepsFixedRowsIntact()
    {
        var service = await FreshAsync();
        await service.RebindAsync(KeyboardShortcutCatalog.Ids.NewChat, new KeyChord(true, true, false, VkJ));

        var groups = service.GetLiveGroups();

        var newChat = groups.Single(g => g.Title == "File").Shortcuts.Single(s => s.Name == "New chat");
        Assert.Equal("Ctrl + Shift + J", newChat.KeyBindings.Single().DisplayText);

        var copy = groups.Single(g => g.Title == "Edit").Shortcuts.Single(s => s.Name == "Copy");
        Assert.False(copy.IsCustomizable);
        Assert.Equal("Ctrl + C", copy.KeyBindings.Single().DisplayText);
        Assert.NotEmpty(copy.ReadOnlyReason);
    }

    [Fact]
    public async Task BindingsChanged_Rebind_FiresSoMenusCanReread()
    {
        var service = await FreshAsync();
        var fired = 0;
        service.BindingsChanged += () => fired++;

        await service.RebindAsync(KeyboardShortcutCatalog.Ids.NewChat, new KeyChord(true, true, false, VkJ));

        Assert.Equal(1, fired);
    }

    private static async Task ClearPreferencesAsync()
    {
        await using var connection = await AppDatabase.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM preferences;";
        await command.ExecuteNonQueryAsync();
    }
}
