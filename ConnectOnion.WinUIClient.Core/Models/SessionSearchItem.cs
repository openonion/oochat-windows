using System;
using System.Linq;
using ConnectOnion.WinUIClient.Common;

namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// WinUI-free projection consumed by the shell's conversation search overlay.
/// </summary>
public sealed class SessionSearchItem : Common.ObservableObject
{
    /// <summary>Longest excerpt kept from a matching message, in chars.</summary>
    private const int SnippetLength = 90;

    public string SessionId { get; init; } = "";
    public string AgentId { get; init; } = "";
    public string Title { get; init; } = "";
    public string AgentName { get; init; } = "";
    public string UpdatedAt { get; init; } = "";

    public string AgentDisplayName => FriendlyAgentName.From(AgentName);

    private string _snippet = "";
    /// <summary>
    /// An excerpt of the message that matched, or empty when this row matched on its title alone.
    /// Set by <see cref="ViewModels.SessionSearchViewModel"/> from the database's content search;
    /// the row shows it so the user can see <i>why</i> a conversation came back.
    /// </summary>
    public string Snippet
    {
        get => _snippet;
        set
        {
            if (!SetProperty(ref _snippet, value)) return;
            OnPropertyChanged(nameof(HasSnippet));
            OnPropertyChanged(nameof(AccessibilityName));
        }
    }

    public bool HasSnippet => Snippet.Length > 0;

    public string AccessibilityName
        => HasSnippet
            ? $"{Title}, {AgentDisplayName}, {Snippet}"
            : $"{Title}, {AgentDisplayName}";

    /// <summary>Matches on the conversation's own metadata — title and agent name. Message
    /// bodies are matched separately, in SQL, because they are not held in memory.</summary>
    public bool Matches(string query)
    {
        var terms = query.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true;

        var searchable = $"{Title}\n{AgentName}\n{AgentDisplayName}";
        return terms.All(term =>
            searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Trims a matching message down to a single line centred on the hit, so the excerpt shows the
    /// query in context rather than the first 90 characters of a long message that happened to
    /// mention it at the end.
    /// </summary>
    public static string BuildSnippet(string content, string query)
    {
        var flattened = string.Join(
            ' ',
            content.Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (flattened.Length <= SnippetLength) return flattened;

        var hit = flattened.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase);
        if (hit < 0) return flattened[..SnippetLength] + "…";

        // Keep a little of what came before the hit so the excerpt reads as a sentence.
        var start = Math.Max(0, hit - 24);
        var length = Math.Min(SnippetLength, flattened.Length - start);
        var excerpt = flattened.Substring(start, length);

        return (start > 0 ? "…" : "")
               + excerpt
               + (start + length < flattened.Length ? "…" : "");
    }
}
