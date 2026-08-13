using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.ViewModels;

/// <summary>
/// Holds a bounded recent/search result window for the shell search overlay and exposes a
/// live-filtered, most-recent-first result list. The overlay only supplies data and writes
/// <see cref="SearchText"/>; matching stays headless and unit-testable.
/// </summary>
public sealed partial class SessionSearchViewModel : Common.ObservableObject
{
    private IReadOnlyList<SessionSearchItem> _allItems = [];
    private IReadOnlyList<SessionSearchItem> _defaultItems = [];

    public ObservableCollection<SessionSearchItem> Results { get; } = new();

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasResults { get; private set; }

    public bool IsEmpty => !HasResults;

    public SessionSearchViewModel()
    {
        SearchText = "";
    }

    /// <summary>Replaces the catalog and clears any query left by a prior opening.</summary>
    public void Reset(IEnumerable<SessionSearchItem> items)
    {
        _defaultItems = items
            .OrderByDescending(item => item.UpdatedAt, StringComparer.Ordinal)
            .ToList();
        _allItems = _defaultItems;

        if (SearchText.Length == 0)
            ApplyFilter();
        else
            SearchText = "";
    }

    partial void OnSearchTextChanged(string value)
    {
        // A new query invalidates whatever content matches the previous one produced. Cleared
        // here rather than when the next batch lands, so results never briefly show an excerpt
        // belonging to a query the user has already moved on from.
        _contentMatches = EmptyMatches;
        _allItems = _defaultItems;
        ApplyFilter();
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMatches =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, string> _contentMatches = EmptyMatches;

    /// <summary>
    /// Folds in the conversations whose <i>messages</i> match, which the in-memory catalog cannot
    /// know about — it holds titles and agent names only. Called by the overlay once the database
    /// query for <paramref name="query"/> returns; a result for a query the user has already typed
    /// past is dropped rather than merged.
    /// </summary>
    public void ApplySearchResults(
        string query,
        IEnumerable<SessionSearchItem> items,
        IReadOnlyDictionary<string, string> matchesBySessionId)
    {
        if (!string.Equals(query.Trim(), SearchText.Trim(), StringComparison.Ordinal)) return;

        _allItems = items
            .OrderByDescending(item => item.UpdatedAt, StringComparer.Ordinal)
            .ToList();
        _contentMatches = matchesBySessionId;
        ApplyFilter();
    }

    /// <summary>Compatibility entry point for headless callers that are only folding transcript
    /// snippets into the current bounded catalog.</summary>
    public void ApplyContentMatches(string query, IReadOnlyDictionary<string, string> matchesBySessionId)
        => ApplySearchResults(query, _allItems, matchesBySessionId);

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        Results.Clear();
        foreach (var item in _allItems)
        {
            var matchedContent = _contentMatches.TryGetValue(item.SessionId, out var content);

            if (!item.Matches(query) && !matchedContent) continue;

            // The excerpt is shown only when the content is what brought the row back. A title
            // match already explains itself, and pinning an excerpt under it would suggest the
            // hit was somewhere else in the conversation.
            item.Snippet = matchedContent && !item.Matches(query)
                ? SessionSearchItem.BuildSnippet(content!, query)
                : "";

            Results.Add(item);
        }

        HasResults = Results.Count > 0;
    }
}
