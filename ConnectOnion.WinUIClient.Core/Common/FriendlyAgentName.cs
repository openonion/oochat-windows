using System;
using System.Globalization;
using System.Text;

namespace ConnectOnion.WinUIClient.Common;

/// <summary>
/// Turns an internal agent identifier into a user-facing display name without
/// mutating the stored id. <c>remote-admin-agent</c> → <c>Remote Admin Agent</c>,
/// <c>multimodal_agent</c> → <c>Multimodal Agent</c>.
///
/// The transform is deliberately conservative: it splits on <c>_</c>/<c>-</c> and
/// whitespace, collapses runs of separators, and upper-cases only the first letter
/// of each word — the rest of a word is left as typed so an intentional acronym
/// (<c>API</c>) or camel-cased name survives rather than being flattened to
/// <c>Api</c>. A name that already looks like prose (contains a space and no
/// snake/kebab separator) is returned unchanged.
/// </summary>
public static class FriendlyAgentName
{
    public static string From(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var trimmed = name.Trim();

        // Already human-friendly (has spaces, no machine separators): leave it alone
        // so a backend-provided display name is never re-cased.
        if (trimmed.Contains(' ') && trimmed.IndexOf('_') < 0 && trimmed.IndexOf('-') < 0)
        {
            return trimmed;
        }

        var builder = new StringBuilder(trimmed.Length + 4);
        var atWordStart = true;

        foreach (var ch in trimmed)
        {
            if (ch == '_' || ch == '-' || char.IsWhiteSpace(ch))
            {
                // Emit a single space between words; swallow repeats and leading separators.
                if (!atWordStart && builder.Length > 0)
                {
                    builder.Append(' ');
                }
                atWordStart = true;
                continue;
            }

            if (atWordStart)
            {
                builder.Append(char.ToUpper(ch, CultureInfo.InvariantCulture));
                atWordStart = false;
            }
            else
            {
                builder.Append(ch);
            }
        }

        var result = builder.ToString().TrimEnd();
        return result.Length == 0 ? trimmed : result;
    }
}
