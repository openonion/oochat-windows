using System.Text.Json;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>
/// Turns an <c>approval_needed</c> request's raw arguments JSON into a safe, compact
/// <see cref="ApprovalTarget"/> for the approval card, so the user can see what is being approved
/// without reading the whole payload.
///
/// <para>Pure and headless by design (it lives in Core and returns data, not XAML): the field
/// priority, the truncation, and the "no target found" fallback are decisions worth unit-testing
/// without a UI thread. The card never renders the untrusted full <c>arguments</c> blob itself —
/// only this extracted, length-capped target — and the raw JSON stays behind the explicit
/// "View operation details" disclosure.</para>
/// </summary>
public static class ApprovalTargetFormatter
{
    // Probed in specificity order: a URL identifies the action better than a path it may also
    // carry, and an explicit command is the whole action. Mirrors ToolActivityProjector's own
    // DisplayTarget priorities so the approval card and the timeline row agree on what a step
    // acted on.
    private static readonly string[] UrlKeys = { "url", "uri", "href" };
    private static readonly string[] PathKeys = { "path", "file", "file_path", "filename", "target", "destination" };
    private static readonly string[] DirectoryKeys = { "directory", "dir", "folder" };
    private static readonly string[] CommandKeys = { "command", "cmd", "script" };

    /// <summary>The chip stays short; the full value lives in its tooltip. A path shows only its
    /// last segment, everything else is truncated with an ellipsis.</summary>
    private const int MaxTargetChars = 72;

    public static ApprovalTarget Extract(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return ApprovalTarget.Empty;

        // Arguments are agent-supplied and not guaranteed to be JSON at all; anything that fails to
        // parse simply yields no target (the card falls back to its generic prompt) rather than
        // throwing on the projection/UI thread.
        JsonElement root;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(argumentsJson); }
        catch { return ApprovalTarget.Empty; }

        using (doc)
        {
            root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ApprovalTarget.Empty;

            foreach (var key in UrlKeys)
            {
                if (Str(root, key) is { Length: > 0 } url)
                {
                    var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
                    return new ApprovalTarget(ApprovalTargetKind.Url, Truncate(host), url);
                }
            }

            foreach (var key in CommandKeys)
            {
                if (Str(root, key) is { Length: > 0 } command)
                {
                    var oneLine = command.Replace('\r', ' ').Replace('\n', ' ').Trim();
                    return new ApprovalTarget(ApprovalTargetKind.Command, Truncate(oneLine), command);
                }
            }

            foreach (var key in PathKeys)
            {
                if (Str(root, key) is { Length: > 0 } path)
                {
                    var name = LastSegment(path);
                    return new ApprovalTarget(ApprovalTargetKind.File, Truncate(name), path);
                }
            }

            foreach (var key in DirectoryKeys)
            {
                if (Str(root, key) is { Length: > 0 } dir)
                {
                    var name = LastSegment(dir);
                    return new ApprovalTarget(ApprovalTargetKind.Directory, Truncate(name), dir);
                }
            }

            // A search phrase or message body: shown so the user knows what is being sent, but with
            // a neutral icon since it is not a resource being acted on.
            if (Str(root, "query") is { Length: > 0 } query)
                return new ApprovalTarget(ApprovalTargetKind.Text, Truncate(query), query);
            if (Str(root, "text") is { Length: > 0 } text)
                return new ApprovalTarget(ApprovalTargetKind.Text, Truncate(text), text);

            return ApprovalTarget.Empty;
        }
    }

    private static string? Str(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The last path segment, so a long absolute path shows as its file name in the chip
    /// while the whole thing stays in the tooltip. Handles both separators.</summary>
    private static string LastSegment(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0) return path;
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 && slash + 1 < normalized.Length ? normalized[(slash + 1)..] : normalized;
    }

    private static string Truncate(string value)
        => value.Length <= MaxTargetChars ? value : value[..(MaxTargetChars - 1)] + "…";
}
