using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.ViewModels;

/// <summary>
/// One row of the Settings → Keyboard list. Customizable rows own an editable chord plus whatever
/// the last rebind attempt had to say; fixed rows carry only the reason they cannot be edited, so
/// the list stays complete without pretending everything on it is rebindable.
/// </summary>
public sealed partial class KeyboardShortcutRow : Common.ObservableObject
{
    private readonly KeyboardShortcutService? _shortcuts;

    public KeyboardShortcutRow(KeyboardShortcutItem item, KeyboardShortcutService? shortcuts)
    {
        _shortcuts = shortcuts;
        Item = item;
        Chord = item.IsCustomizable && shortcuts is not null
            ? shortcuts.GetChord(item.Id)
            : KeyChord.None;
        Conflict = "";
        Refresh();
    }

    public KeyboardShortcutItem Item { get; }

    public string Id => Item.Id;
    public string Name => Item.Name;
    public bool IsCustomizable => Item.IsCustomizable;
    public string ReadOnlyReason => Item.ReadOnlyReason;

    /// <summary>The chord this row currently shows. <see cref="KeyChord.None"/> for a fixed row,
    /// which renders <see cref="Item"/>'s catalog keycaps instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Binding))]
    public partial KeyChord Chord { get; private set; }

    /// <summary>Keycaps for the current chord. A fixed row has no chord, so it falls back to the
    /// catalog's own keycaps — that is the only binding it will ever have.</summary>
    public KeyBinding Binding => Chord.IsEmpty
        ? (Item.KeyBindings.Count > 0 ? Item.KeyBindings[0] : new KeyBinding())
        : Chord.ToBinding();

    /// <summary>Why the last rebind was refused, or empty. Drives the inline warning.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflict))]
    public partial string Conflict { get; private set; }

    public bool HasConflict => Conflict.Length > 0;

    /// <summary>True when this row is off its factory chord — gates the per-row Reset affordance.</summary>
    [ObservableProperty]
    public partial bool IsRebound { get; private set; }

    /// <summary>Attempts a rebind. A refusal leaves the live binding alone and reports why, so the
    /// user can pick again rather than silently stealing another action's chord.</summary>
    public async Task<bool> TryRebindAsync(KeyChord chord)
    {
        if (_shortcuts is null || !IsCustomizable || chord.IsEmpty) return false;

        var result = await _shortcuts.RebindAsync(Id, chord).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            Conflict = result.Outcome == RebindOutcome.Conflict
                ? $"Already used by “{result.ConflictingActionName}”"
                : "That key can't be used for a shortcut";
            return false;
        }

        Conflict = "";
        Refresh();
        return true;
    }

    public async Task ResetAsync()
    {
        if (_shortcuts is null || !IsCustomizable) return;

        await _shortcuts.ResetAsync(Id).ConfigureAwait(true);
        Conflict = "";
        Refresh();
    }

    public void ClearConflict() => Conflict = "";

    /// <summary>Re-reads the live chord. Called after this row changes it, and after anything else
    /// does (a reset-all, or the other surface).</summary>
    public void Refresh()
    {
        if (_shortcuts is null || !IsCustomizable) return;

        Chord = _shortcuts.GetChord(Id);
        IsRebound = _shortcuts.IsRebound(Id);
    }
}

/// <summary>A titled run of rows, mirroring the catalog's grouping.</summary>
public sealed class KeyboardShortcutRowGroup
{
    public KeyboardShortcutRowGroup(string title, IReadOnlyList<KeyboardShortcutRow> rows)
    {
        Title = title;
        Rows = rows;
    }

    public string Title { get; }
    public IReadOnlyList<KeyboardShortcutRow> Rows { get; }
}

/// <summary>
/// Backs Settings → Keyboard: the same catalog the Ctrl+Shift+/ dialog shows, but with the
/// customizable rows editable. Search matches the dialog's behaviour exactly (a group-title hit
/// reveals the whole group, otherwise only matching rows) because it runs through the same
/// <see cref="KeyboardShortcutItem.Matches"/>.
/// </summary>
public sealed partial class KeyboardSettingsViewModel : Common.ObservableObject
{
    private readonly KeyboardShortcutService? _shortcuts;
    private readonly List<KeyboardShortcutRowGroup> _allGroups = new();

    public ObservableCollection<KeyboardShortcutRowGroup> Groups { get; } = new();

    [ObservableProperty]
    public partial string SearchText { get; set; }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasResults { get; private set; }

    public bool IsEmpty => !HasResults;

    /// <summary>True when anything at all is off its default — gates "Reset all".</summary>
    [ObservableProperty]
    public partial bool HasAnyRebinding { get; private set; }

    public KeyboardSettingsViewModel() : this(null)
    {
    }

    public KeyboardSettingsViewModel(KeyboardShortcutService? shortcuts)
    {
        _shortcuts = shortcuts;

        var source = shortcuts?.GetLiveGroups() ?? KeyboardShortcutCatalog.GetGroups();
        foreach (var group in source)
        {
            var rows = group.Shortcuts.Select(item => new KeyboardShortcutRow(item, shortcuts)).ToList();
            _allGroups.Add(new KeyboardShortcutRowGroup(group.Title, rows));
        }

        SearchText = "";
        RefreshRebindingState();
    }

    /// <summary>Clears the search so a re-opened page starts from the whole list.</summary>
    public void Reset() => SearchText = "";

    /// <summary>Drops every override at once, then re-reads each row.</summary>
    public async Task ResetAllAsync()
    {
        if (_shortcuts is null) return;

        await _shortcuts.ResetAllAsync().ConfigureAwait(true);
        RefreshRows();
    }

    /// <summary>Re-reads every row's live chord — after a reset-all, or after a rebind that could
    /// have freed a chord another row was reporting a conflict against.</summary>
    public void RefreshRows()
    {
        foreach (var row in _allGroups.SelectMany(g => g.Rows))
        {
            row.ClearConflict();
            row.Refresh();
        }
        RefreshRebindingState();
    }

    public void RefreshRebindingState()
        => HasAnyRebinding = _allGroups.SelectMany(g => g.Rows).Any(r => r.IsRebound);

    private void ApplyFilter()
    {
        var query = SearchText.Trim();

        Groups.Clear();
        foreach (var group in _allGroups)
        {
            IReadOnlyList<KeyboardShortcutRow> matches;
            if (query.Length == 0 || group.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                matches = group.Rows;
            else
                matches = group.Rows.Where(r => r.Item.Matches(query)).ToList();

            if (matches.Count > 0)
                Groups.Add(new KeyboardShortcutRowGroup(group.Title, matches));
        }

        HasResults = Groups.Count > 0;
    }
}
