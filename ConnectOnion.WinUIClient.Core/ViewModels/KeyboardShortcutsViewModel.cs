using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.ViewModels;

/// <summary>
/// Backs the Keyboard shortcuts dialog: holds the full catalog and exposes a live, search-
/// filtered view of it. The UI only reads <see cref="Groups"/> / <see cref="IsEmpty"/> and
/// writes <see cref="SearchText"/> — all filtering lives here, not in code-behind.
///
/// <para>Read-only by design: this is the quick-reference overview. Rebinding lives in
/// Settings → Keyboard (<see cref="KeyboardSettingsViewModel"/>). Both read the same catalog and
/// the same <see cref="KeyboardShortcutService"/>, so what this dialog shows is what actually
/// fires — including anything the user rebound.</para>
/// </summary>
public sealed partial class KeyboardShortcutsViewModel : Common.ObservableObject
{
    private readonly IReadOnlyList<KeyboardShortcutGroup> _allGroups;

    /// <summary>The groups (with their matching items) to render for the current search.</summary>
    public ObservableCollection<KeyboardShortcutGroup> Groups { get; } = new();

    [ObservableProperty]
    public partial string SearchText { get; set; }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasResults { get; private set; }

    /// <summary>True when the current search matched nothing — drives the empty state.</summary>
    public bool IsEmpty => !HasResults;

    /// <summary>Catalog defaults only. Kept for the dialog's own construction and for tests that
    /// exercise filtering without a container behind them.</summary>
    public KeyboardShortcutsViewModel() : this(null)
    {
    }

    /// <param name="shortcuts">When supplied, rows show the chord that is actually live rather
    /// than the factory default.</param>
    public KeyboardShortcutsViewModel(KeyboardShortcutService? shortcuts)
    {
        _allGroups = shortcuts?.GetLiveGroups() ?? KeyboardShortcutCatalog.GetGroups();
        // Setting SearchText fires OnSearchTextChanged → ApplyFilter, which seeds Groups and HasResults.
        SearchText = "";
    }

    /// <summary>Clears the search back to the full list. Called each time the dialog opens so a
    /// prior session's query never leaks into a fresh view.</summary>
    public void Reset() => SearchText = "";

    private void ApplyFilter()
    {
        var query = SearchText.Trim();

        Groups.Clear();
        foreach (var group in _allGroups)
        {
            // A group-title match reveals the whole group; otherwise only its matching items.
            IReadOnlyList<KeyboardShortcutItem> matches;
            if (query.Length == 0 || group.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                matches = group.Shortcuts;
            else
                matches = group.Shortcuts.Where(s => s.Matches(query)).ToList();

            if (matches.Count > 0)
                Groups.Add(new KeyboardShortcutGroup(group.Title, matches));
        }

        HasResults = Groups.Count > 0;
    }
}
