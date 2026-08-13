using System.Text;
using System.Text.RegularExpressions;

namespace ConnectOnion.WinUIClient.Services.Notifications;

/// <summary>
/// Turns raw agent/markdown content into short, safe notification text. Never
/// let a full markdown body, code block, tool-call JSON, or an over-long message
/// reach the OS toast — strip formatting, collapse whitespace, and truncate.
/// </summary>
public static partial class NotificationText
{
    public const int MaxPreviewLength = 140;

    /// <summary>Generic body shown when message preview is disabled, per type.</summary>
    public static string GenericBody(Models.Notifications.NotificationType type) => type switch
    {
        Models.Notifications.NotificationType.AgentReply => "You have a new response.",
        Models.Notifications.NotificationType.TaskCompleted => "A task has finished.",
        Models.Notifications.NotificationType.ApprovalRequired => "An action needs your approval.",
        Models.Notifications.NotificationType.ConnectionLost => "An agent went offline.",
        _ => "Something needs your attention.",
    };

    /// <summary>Sanitizes and truncates arbitrary (possibly markdown) text for a toast body.</summary>
    public static string Preview(string? raw, int max = MaxPreviewLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var text = StripMarkdown(raw);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        if (text.Length <= max) return text;
        return text[..max].TrimEnd() + "…";
    }

    private static string StripMarkdown(string s)
    {
        // Fenced code blocks → placeholder (never dump code into a toast).
        s = FencedCodeRegex().Replace(s, " [code] ");
        // Images ![alt](url) → alt.
        s = ImageRegex().Replace(s, "$1");
        // Links [text](url) → text.
        s = LinkRegex().Replace(s, "$1");
        // Inline code `x` → x.
        s = InlineCodeRegex().Replace(s, "$1");

        var sb = new StringBuilder(s.Length);
        foreach (var rawLine in s.Split('\n'))
        {
            var line = rawLine.TrimStart();
            // Headings / blockquotes / list markers at line start.
            line = LineMarkerRegex().Replace(line, "");
            sb.Append(line).Append(' ');
        }

        var text = sb.ToString();
        // Emphasis / strikethrough markers.
        text = EmphasisRegex().Replace(text, "");
        return text;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"```[\s\S]*?```|~~~[\s\S]*?~~~")]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"`([^`]*)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"^\s{0,3}(#{1,6}\s+|>\s?|[-*+]\s+|\d+\.\s+)")]
    private static partial Regex LineMarkerRegex();

    [GeneratedRegex(@"[*_~]{1,3}")]
    private static partial Regex EmphasisRegex();
}
