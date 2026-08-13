using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// What kind of thing a tool step was *given* — the shell command, the search pattern, the URL.
/// Drives how the timeline renders the invocation line, the way <see cref="ToolIconKind"/> drives
/// the glyph.
/// </summary>
public enum ToolInvocationKind
{
    /// <summary>Nothing worth showing: the arguments were empty, unparseable, or carried no field
    /// this reader recognizes. The step renders exactly as it did before invocations existed.</summary>
    None = 0,
    /// <summary>A shell command line.</summary>
    Command,
    /// <summary>A search pattern or query.</summary>
    Search,
    /// <summary>A file or directory path.</summary>
    Path,
    /// <summary>A URL.</summary>
    Url,
    /// <summary>Work handed to a sub-agent or a background job.</summary>
    Task,
    /// <summary>Free text the tool acted on or typed.</summary>
    Text,
}

/// <summary>
/// The one-line "what was this tool actually asked to do" shown on a timeline step.
/// </summary>
/// <param name="Kind">Which family it belongs to; the view styles by this.</param>
/// <param name="Label">Heading for the expanded block ("Command", "Pattern", ...).</param>
/// <param name="Text">The value itself, rendered monospace.</param>
/// <param name="Prefix">Rendered before <paramref name="Text"/> — a shell <c>$</c> for a command,
/// empty otherwise. Kept here rather than in the view so the "does this look like a terminal"
/// decision is testable along with the rest of the classification. Carries no trailing space: XAML
/// collapses whitespace between adjacent inlines, so the gap has to come from layout (a panel's
/// Spacing) or from <see cref="ToolInvocation.DisplayText"/>, never from padding this string.</param>
/// <param name="Secondary">An optional second line (a sub-agent's prompt, a search's directory).</param>
public sealed record ToolInvocation(
    ToolInvocationKind Kind,
    string Label,
    string Text,
    string Prefix = "",
    string? Secondary = null)
{
    public static readonly ToolInvocation None = new(ToolInvocationKind.None, "", "");

    public bool HasValue => Kind != ToolInvocationKind.None && !string.IsNullOrWhiteSpace(Text);
    public bool HasSecondary => !string.IsNullOrWhiteSpace(Secondary);

    /// <summary>Prefix and text as one string, for the wrapping block where they cannot be two
    /// elements. One string rather than two adjacent <c>Run</c>s because XAML collapses the
    /// whitespace between inlines and the command renders as <c>$rm</c>.</summary>
    public string DisplayText => Prefix.Length == 0 ? Text : $"{Prefix} {Text}";

    /// <summary>Longest single-line preview kept for the collapsed row. Past this the row is
    /// already ellipsized on every realistic width, and the difference is only how much text
    /// <c>TextBlock</c> has to measure — which matters when an argument is a whole document.</summary>
    private const int SingleLinePreviewLength = 300;

    /// <summary>
    /// <see cref="Text"/> flattened to one line, for the collapsed step row.
    ///
    /// <para>The row is a fixed one-scan-height slot and its <c>TextBlock</c> declares
    /// <c>TextWrapping="NoWrap"</c> — but NoWrap only suppresses <i>automatic</i> wrapping. A
    /// literal newline in the value still breaks the line, so a tool whose argument is a whole
    /// markdown document (<c>write_plan</c> hands over the entire plan) unfolded across the
    /// transcript the moment its step was <b>collapsed</b>, which is the only state that shows
    /// this row. Ellipsizing cannot help either: it applies per line.</para>
    ///
    /// <para>Only the collapsed row uses this. <see cref="Text"/> and <see cref="DisplayText"/>
    /// stay verbatim for the expanded block, where the full value is the point.</para>
    /// </summary>
    public string SingleLineText => Flatten(Text);

    private static string Flatten(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var builder = new System.Text.StringBuilder(
            Math.Min(value.Length, SingleLinePreviewLength));
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                // Collapse any run of whitespace — including the newlines that caused this — into
                // the single space that separates the tokens around it.
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (builder.Length >= SingleLinePreviewLength) break;
                builder.Append(' ');
                pendingSpace = false;
            }

            if (builder.Length >= SingleLinePreviewLength) break;
            builder.Append(ch);
        }

        // The ellipsis is only added when text was actually dropped, so a value that merely had
        // its newlines collapsed does not claim to be truncated.
        return builder.Length >= SingleLinePreviewLength ? builder.Append('…').ToString() : builder.ToString();
    }
}

/// <summary>
/// Reads a step's invocation out of its recorded arguments.
///
/// <para><b>Why this exists.</b> The timeline used to show a tool's <i>name</i> and its <i>result</i>
/// and nothing in between, so a step could say "Run command · 240 ms" and never reveal which command
/// ran — the single most useful fact about it. The arguments were captured and persisted all along;
/// nothing rendered them.</para>
///
/// <para><b>Why it is derived at render time rather than stored.</b> Same reasoning as
/// <see cref="ToolIcons"/>: <c>Arguments</c> is already on every persisted step, so deriving from it
/// costs no column, needs no migration, and — the point — conversations recorded before this existed
/// show their commands the moment they are reopened. Storing a projected line instead would light up
/// new turns only.</para>
///
/// <para><b>Two passes, because tool names are not a controlled vocabulary</b> (again as in
/// <see cref="ToolIcons"/>): an exact table pins the tools the reference agents ship, then an ordered
/// keyword scan catches the <c>remote_</c>/<c>fs.</c>-prefixed variants of the same capabilities. A
/// tool that matches nothing still gets a generic argument probe, so a custom tool with a
/// <c>url</c> or <c>path</c> argument reads correctly without being listed anywhere.</para>
/// </summary>
public static class ToolInvocations
{
    // Argument names probed per family, in order. First present, non-empty string wins.
    private static readonly string[] CommandArgs = ["command", "cmd", "script", "shell_command", "code"];
    private static readonly string[] SearchArgs = ["pattern", "query", "q", "search", "keyword", "regex", "glob"];
    private static readonly string[] PathArgs =
        ["path", "file_path", "file", "filename", "guide_path", "directory", "dir"];
    private static readonly string[] UrlArgs = ["url", "uri", "href", "link"];
    private static readonly string[] TextArgs = ["text", "description", "selector", "message", "content", "prompt"];

    /// <summary>
    /// Browser tools, mirroring the reference web client's set. Membership matters because these
    /// share one shape — a verb plus a target that is a URL, a selector, or a human description —
    /// which no generic probe orders correctly (a click carries both a <c>selector</c> and a
    /// <c>description</c>, and the description is the one a person can read).
    /// </summary>
    private static readonly HashSet<string> BrowserTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "go_to", "open_browser", "newtab", "close_tab", "get_current_url",
        "click", "double_click", "right_click", "hover", "mouse_click",
        "scroll", "wait", "set_viewport",
        "take_screenshot", "save_page_context", "save_state",
        "run_page_script", "run_frame_script",
        "extract_data", "extract_items_by_selector", "get_text", "get_links_from_page",
        "get_element_text_by_selector", "count_elements_by_selector", "find_element_by_description",
        "type_text_by_selector", "keyboard_type", "keyboard_press",
        "select_option", "check_checkbox",
        "click_element_by_selector", "click_element_near_selector",
        "upload_file_by_selector", "upload_file_after_click_by_selector",
    };

    private enum Family { Unknown, Command, Search, File, Browser, Task }

    /// <summary>Exact tool name → family. Runs before the keyword scan, and is also where a name
    /// whose keywords would mislead gets its answer.</summary>
    private static readonly Dictionary<string, Family> ExactFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bash"] = Family.Command,
        ["shell"] = Family.Command,
        ["run"] = Family.Command,
        ["run_background"] = Family.Command,
        ["run_command"] = Family.Command,
        ["execute_command"] = Family.Command,
        ["execute"] = Family.Command,

        ["grep"] = Family.Search,
        ["glob"] = Family.Search,
        ["search"] = Family.Search,
        ["find"] = Family.Search,
        ["search_web"] = Family.Search,
        ["web_search"] = Family.Search,

        ["read"] = Family.File,
        ["write"] = Family.File,
        ["edit"] = Family.File,
        ["read_file"] = Family.File,
        ["write_file"] = Family.File,
        ["delete_file"] = Family.File,
        ["upload_file"] = Family.File,
        ["download_file"] = Family.File,

        ["call_omo_agent"] = Family.Task,
        ["background_output"] = Family.Task,
        ["background_cancel"] = Family.Task,
        ["task"] = Family.Task,

        ["load_guide"] = Family.File,
    };

    /// <summary>
    /// Tools whose <i>result</i> is documentation written in markdown rather than program output.
    ///
    /// <para>Kept as an explicit short list, not a heuristic. The step's result block is monospace
    /// on purpose — a shell transcript or a stack trace depends on its whitespace — so switching to
    /// a prose renderer has to be something a tool opts into, never something guessed from content
    /// that happens to contain a <c>#</c>.</para>
    /// </summary>
    private static readonly HashSet<string> MarkdownResultTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "load_guide", "read_guide", "get_guide",
        // Plan mode's writer hands back the plan it just wrote — a markdown document with
        // headings, tables and rules, which the log block rendered as literal '#', '|' and '---'.
        // Listed by exact name rather than by a "plan" keyword, because `exit_plan_and_implement`
        // and friends also carry that word and their results really are status reports.
        "write_plan", "update_plan",
    };

    /// <summary>Whether this tool's result should render as markdown instead of as a monospace log.</summary>
    public static bool ProducesMarkdown(string? toolName)
        => !string.IsNullOrEmpty(toolName)
            && (MarkdownResultTools.Contains(toolName)
                // Same two-pass shape as the family lookup: remote_load_guide is still a guide.
                || toolName.Contains("guide", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Tools whose <i>result</i> is the content they were asked to fetch — a file's text, a
    /// document, a page's body, a list of search hits — rather than a report about what happened.
    ///
    /// <para><b>This is what stops a read from being painted as a failure.</b> Most tools never set
    /// a status field and describe their problems in prose, so <c>ToolActivityProjector.Classify</c>
    /// scans the result text for words like "failed", "exception" and "timeout". For an action that
    /// is a fair inference. For a tool that hands back a document it is a category error: the words
    /// belong to the document, not to the tool. Reading a system prompt that explains error
    /// handling, a log file, or a source file with a `catch` block would mark the step red, and a
    /// file mentioning a 404 anywhere would mark it amber.</para>
    ///
    /// <para>Explicit list plus a token-boundary keyword pass, exactly like
    /// <see cref="MarkdownResultTools"/> — never inferred from the result, because "does this text
    /// look like an error" is precisely the question that cannot be answered by looking at it.
    /// Writes and deletes are deliberately absent: their result really is a status report.</para>
    /// </summary>
    private static readonly HashSet<string> ContentResultTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read", "read_file", "cat", "view", "view_file", "open_file", "get_file", "head", "tail",
        "grep", "glob", "search", "find", "search_files", "search_web", "web_search",
        "get_text", "get_page_text", "read_page", "get_page_content", "extract_data",
        "get_element_text_by_selector", "get_links_from_page",
    };

    /// <summary>
    /// Matches "read"/"grep" at the start of a name segment, so prefixed variants
    /// (<c>remote_read_file</c>, <c>fs.readFile</c>) are covered without catching names that merely
    /// contain the letters — <c>create_thread</c> has "read" inside "thread" and must not qualify.
    ///
    /// <para><c>\b</c> is deliberately <b>not</b> used: <c>_</c> is a word character in .NET regex,
    /// so <c>\bread</c> finds no boundary in <c>remote_read_file</c> — the exact case this pattern
    /// exists to cover. "Start of string, or preceded by a non-letter" is the rule that actually
    /// separates tool-name segments.</para>
    /// </summary>
    private static readonly Regex ContentResultKeywords =
        new(@"(?:^|[^A-Za-z])(?:read|grep)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Whether this tool's result is content to be shown, not a status to be judged.
    /// Callers must not run failure/warning prose heuristics against such a result.</summary>
    public static bool ReturnsContent(string? toolName)
        => !string.IsNullOrEmpty(toolName)
            && (ContentResultTools.Contains(toolName)
                || ProducesMarkdown(toolName)
                || ContentResultKeywords.IsMatch(toolName));

    /// <summary>Ordered keyword fallback; first substring hit wins. Ordered most-identifying first
    /// for the same reason <see cref="ToolIcons"/>'s list is: real names carry several matching
    /// words at once, and <c>search_files</c> is a search before it is a file.</summary>
    private static readonly (string Keyword, Family Family)[] KeywordFamilies =
    [
        ("command", Family.Command),
        ("bash", Family.Command),
        ("shell", Family.Command),
        ("exec", Family.Command),
        ("subagent", Family.Task),
        ("agent", Family.Task),
        ("background", Family.Task),
        ("grep", Family.Search),
        ("glob", Family.Search),
        ("search", Family.Search),
        ("query", Family.Search),
        ("browser", Family.Browser),
        ("page", Family.Browser),
        ("file", Family.File),
        ("path", Family.File),
        ("dir", Family.File),
    ];

    /// <summary>
    /// The invocation for a step, or <see cref="ToolInvocation.None"/> when there is nothing to show.
    /// </summary>
    /// <param name="toolName">The raw tool name off the wire.</param>
    /// <param name="argumentsJson">The step's recorded arguments — already redacted by
    /// <c>ToolActivityProjector.SanitizeJson</c>, which is why this can render them verbatim.</param>
    public static ToolInvocation Read(string? toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return ToolInvocation.None;

        JsonElement root;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            // Arguments that are not JSON at all (SanitizeJson falls back to scrubbed text). There
            // is no field to name, so showing the blob under a made-up heading would mislead.
            return ToolInvocation.None;
        }

        using (document)
        {
            root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ToolInvocation.None;

            var name = toolName ?? "";
            return FamilyOf(name) switch
            {
                Family.Command => FromArgs(root, CommandArgs, ToolInvocationKind.Command, "Command", "$")
                    ?? Generic(root),
                Family.Search => ReadSearch(root) ?? Generic(root),
                Family.File => FromArgs(root, PathArgs, ToolInvocationKind.Path, "Path")
                    ?? Generic(root),
                Family.Browser => ReadBrowser(root),
                Family.Task => ReadTask(root) ?? Generic(root),
                _ => Generic(root),
            };
        }
    }

    private static Family FamilyOf(string toolName)
    {
        if (BrowserTools.Contains(toolName)) return Family.Browser;
        if (ExactFamilies.TryGetValue(toolName, out var exact)) return exact;

        var normalized = toolName.ToLowerInvariant();
        foreach (var (keyword, family) in KeywordFamilies)
        {
            if (normalized.Contains(keyword, StringComparison.Ordinal)) return family;
        }
        return Family.Unknown;
    }

    /// <summary>A search shows what was searched for, and — as a second line — where, because
    /// "TODO" alone does not say whether it swept one file or the whole tree.</summary>
    private static ToolInvocation? ReadSearch(JsonElement root)
    {
        if (FromArgs(root, SearchArgs, ToolInvocationKind.Search, "Pattern") is not { } found) return null;
        var scope = FirstString(root, PathArgs);
        return string.IsNullOrWhiteSpace(scope) ? found : found with { Secondary = $"in {scope}" };
    }

    /// <summary>
    /// A browser step prefers the URL, then a human description, then the raw selector.
    ///
    /// <para>That order is the whole point of treating browsers as a family: a click argument object
    /// usually carries <c>selector</c> <i>and</i> <c>description</c>, and a generic probe would print
    /// <c>div.card:nth-child(3) &gt; button</c> where the agent had already written "the Sign in
    /// button".</para>
    /// </summary>
    private static ToolInvocation ReadBrowser(JsonElement root)
    {
        if (FromArgs(root, UrlArgs, ToolInvocationKind.Url, "URL") is { } url) return url;
        if (FirstString(root, ["description"]) is { Length: > 0 } description)
            return new ToolInvocation(ToolInvocationKind.Text, "Target", description);
        if (FirstString(root, ["text", "key", "option", "selector", "container_selector", "target_selector"])
            is { Length: > 0 } value)
        {
            return new ToolInvocation(ToolInvocationKind.Text, "Target", value);
        }
        return ToolInvocation.None;
    }

    /// <summary>Delegated work: the headline is what the sub-agent was asked to do, with the full
    /// prompt underneath when the two differ.</summary>
    private static ToolInvocation? ReadTask(JsonElement root)
    {
        var headline = FirstString(root, ["description", "task", "goal", "prompt", "task_id"]);
        if (string.IsNullOrWhiteSpace(headline)) return null;

        var prompt = FirstString(root, ["prompt"]);
        var secondary = !string.IsNullOrWhiteSpace(prompt)
            && !string.Equals(prompt, headline, StringComparison.Ordinal)
                ? prompt
                : null;
        return new ToolInvocation(ToolInvocationKind.Task, "Task", headline!, Secondary: secondary);
    }

    /// <summary>The fallback probe for an unrecognized tool, in specificity order. A tool nobody
    /// listed still shows its URL or path rather than nothing.</summary>
    private static ToolInvocation Generic(JsonElement root)
        => FromArgs(root, UrlArgs, ToolInvocationKind.Url, "URL")
            ?? FromArgs(root, CommandArgs, ToolInvocationKind.Command, "Command", "$")
            ?? FromArgs(root, PathArgs, ToolInvocationKind.Path, "Path")
            ?? FromArgs(root, SearchArgs, ToolInvocationKind.Search, "Query")
            ?? FromArgs(root, TextArgs, ToolInvocationKind.Text, "Input")
            ?? ToolInvocation.None;

    private static ToolInvocation? FromArgs(
        JsonElement root, string[] names, ToolInvocationKind kind, string label, string prefix = "")
        => FirstString(root, names) is { Length: > 0 } value
            ? new ToolInvocation(kind, label, value, prefix)
            : null;

    /// <summary>
    /// First property from <paramref name="names"/> that is present and non-blank.
    ///
    /// <para>Strings only, and deliberately so: a number or object in a <c>command</c> slot is a
    /// malformed frame, and rendering <c>{"a":1}</c> under a "Command" heading invents a fact. The
    /// step still shows its name and result, exactly as before.</para>
    /// </summary>
    private static string? FirstString(JsonElement root, string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { } text
                && !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }
        return null;
    }
}
