using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>What a piece of a formatted tool result is, so the view can colour it.</summary>
public enum ResultTokenKind
{
    /// <summary>Anything with no syntax meaning — all of a plain-text result, whitespace and
    /// indentation in a JSON one.</summary>
    Plain,
    /// <summary>A JSON object's property name (without its quotes).</summary>
    Key,
    /// <summary>A JSON string value, quotes included.</summary>
    StringLiteral,
    Number,
    /// <summary><c>true</c>, <c>false</c>, or <c>null</c>.</summary>
    Keyword,
    /// <summary>Braces, brackets, commas, colons.</summary>
    Punctuation,
    /// <summary>A shell command line in console output — the <c>$ hostname</c> form. The single
    /// most useful anchor in a command transcript: it is where each block of output begins.</summary>
    Command,
    /// <summary>A section label introducing the lines under it (<c>stdout:</c>, <c>stderr:</c>).</summary>
    Label,
    /// <summary>A non-zero exit status, or a line announcing a failure. Coloured as a warning so
    /// a failed step in a long transcript is findable without reading it.</summary>
    Failure,
}

/// <summary>Which of the three shapes a result turned out to be, so the view can set spacing.</summary>
public enum ResultFormat
{
    /// <summary>Free-form text with no recognized structure.</summary>
    PlainText,
    /// <summary>JSON, re-indented by this formatter.</summary>
    Json,
    /// <summary>Command/console output, coloured in place but never re-laid-out.</summary>
    Console,
}

/// <param name="Text">The literal characters to draw.</param>
/// <param name="Kind">How to colour them.</param>
public readonly record struct ResultToken(string Text, ResultTokenKind Kind);

/// <param name="Tokens">The result, split into colourable runs. Concatenating every
/// <see cref="ResultToken.Text"/> reproduces the displayed text exactly.</param>
/// <param name="Format">Which shape was recognized; the view uses it for line spacing.</param>
public sealed record FormattedToolResult(IReadOnlyList<ResultToken> Tokens, ResultFormat Format)
{
    /// <summary>True for the one format this formatter actually re-lays-out. Console output is
    /// recognized and coloured but its layout is never touched.</summary>
    public bool IsStructured => Format == ResultFormat.Json;
}

/// <summary>
/// Turns a tool result's raw text into display runs.
///
/// <para>Tool results arrive as one opaque string, but they are not one kind of thing, and the
/// three shapes want opposite treatment:</para>
/// <list type="bullet">
/// <item><b>JSON</b> arrives minified — unreadable as one wrapped monospace line — so it is
/// re-indented and typed for colouring. This is the only case whose layout is changed.</item>
/// <item><b>Console transcripts</b> (<c>$ cmd</c>, <c>stdout:</c>, <c>exit_code=0</c>) are
/// already laid out, often in aligned columns. Their structural lines are coloured
/// <i>in place</i>; not one character moves.</item>
/// <item><b>Everything else</b> passes through as a single unaltered
/// <see cref="ResultTokenKind.Plain"/> token.</item>
/// </list>
/// <para>In every case, concatenating the tokens' text reproduces what is displayed — and for
/// the latter two, that is the input verbatim.</para>
///
/// <para>Lives in Core, and returns tokens rather than XAML <c>Inline</c>s, precisely so the
/// decisions here — what counts as structured, how it indents, where each run starts and stops —
/// are testable without a UI thread.</para>
/// </summary>
public static class ToolResultFormatter
{
    /// <summary>Above this, the input is not probed for JSON at all. Re-parsing and re-indenting
    /// a very large payload costs more than the readability is worth for a body the card shows in
    /// a ~330px scroller, and the projector has already capped what it stores.</summary>
    private const int MaxStructuredInputLength = 200_000;

    /// <summary>Guards against a pathological nesting depth turning the writer loose on the UI
    /// thread. Beyond this the text is treated as plain rather than re-indented.</summary>
    private const int MaxStructuredDepth = 32;

    public static FormattedToolResult Format(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new FormattedToolResult(Array.Empty<ResultToken>(), ResultFormat.PlainText);

        if (TryFormatJson(text, out var jsonTokens))
            return new FormattedToolResult(jsonTokens, ResultFormat.Json);

        // Console output keeps every character and every column exactly where the tool put it — a
        // `ps` listing or a diff is aligned text and re-laying it out would destroy it. The only
        // thing added is colour on the lines that carry structure.
        if (TryFormatConsole(text, out var consoleTokens))
            return new FormattedToolResult(consoleTokens, ResultFormat.Console);

        // Nothing recognized: one run, unaltered.
        return new FormattedToolResult(
            new[] { new ResultToken(text, ResultTokenKind.Plain) }, ResultFormat.PlainText);
    }

    /// <summary>Lines that open a command block: <c>$ hostname</c>, <c>&gt; npm test</c>.</summary>
    private static readonly Regex CommandLine = new(@"^\s*[$>#]\s+\S", RegexOptions.Compiled);

    /// <summary>A section label alone on its line: <c>stdout:</c>, <c>stderr:</c>.</summary>
    private static readonly Regex LabelLine = new(
        @"^\s*(stdout|stderr|output|error|traceback|warnings?)\s*:\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A <c>key=value</c> metadata line. Anchored, with an identifier-shaped key, so it
    /// does not fire on prose that happens to contain an equals sign.</summary>
    private static readonly Regex KeyValueLine = new(
        @"^(\s*)([A-Za-z_][A-Za-z0-9_.-]*)(=)(.*)$", RegexOptions.Compiled);

    /// <summary>
    /// Recognizes a command/console transcript and colours its structural lines in place.
    ///
    /// <para>Two structural lines is the bar, but at least one of them must be a
    /// <i>distinctive</i> console anchor: a <c>$</c> shell prompt, a <c>stdout:</c>/<c>stderr:</c>
    /// label, or an exit/return/status line. A generic <c>key=value</c> and a markdown-ish
    /// <c>#</c>/<c>&gt;</c> line are deliberately <b>weak</b> — a URL query string
    /// (<c>utm_source=google</c>), a config snippet, a markdown heading (<c>#&#160;Title</c>) and a
    /// blockquote (<c>&gt;&#160;quote</c>) all look identical to them — so two of those on their
    /// own no longer flip a fetched web page or a file listing into a "transcript" whose filenames
    /// and links then get recoloured. Weak lines still get coloured once a real anchor has claimed
    /// the format; they just can't establish it by themselves.</para>
    /// </summary>
    private static bool TryFormatConsole(string text, out IReadOnlyList<ResultToken> tokens)
    {
        tokens = Array.Empty<ResultToken>();
        if (text.Length > MaxStructuredInputLength) return false;

        var lines = SplitKeepingLineEndings(text);
        var collected = new List<ResultToken>(lines.Count * 2);
        var recognized = 0;
        // Distinctive console markers, unlikely to appear in prose/markdown/URLs by accident.
        var anchors = 0;

        foreach (var (line, ending) in lines)
        {
            if (CommandLine.IsMatch(line))
            {
                collected.Add(new ResultToken(line, ResultTokenKind.Command));
                recognized++;
                // Only a `$ ` shell prompt anchors the format; markdown `# heading` and
                // `> quote` share the `#`/`>` markers, so they stay weak.
                if (IsShellPrompt(line)) anchors++;
            }
            else if (LabelLine.IsMatch(line))
            {
                collected.Add(new ResultToken(line, ResultTokenKind.Label));
                recognized++;
                anchors++;
            }
            else if (KeyValueLine.Match(line) is { Success: true } kv)
            {
                var key = kv.Groups[2].Value;
                var value = kv.Groups[4].Value;
                // exit_code=0 is noise; exit_code=1 is the whole story.
                var failed = IsFailureStatus(key, value);

                collected.Add(new ResultToken(kv.Groups[1].Value, ResultTokenKind.Plain));
                collected.Add(new ResultToken(key, failed ? ResultTokenKind.Failure : ResultTokenKind.Key));
                collected.Add(new ResultToken("=", ResultTokenKind.Punctuation));
                collected.Add(new ResultToken(value, failed ? ResultTokenKind.Failure : ResultTokenKind.Plain));
                recognized++;
                // A bare `foo=bar` is weak; only an exit/return/status line is a console anchor.
                if (IsStatusKey(key)) anchors++;
            }
            else
            {
                collected.Add(new ResultToken(line, ResultTokenKind.Plain));
            }

            if (ending.Length > 0) collected.Add(new ResultToken(ending, ResultTokenKind.Plain));
        }

        if (recognized < 2 || anchors < 1) return false;

        tokens = collected;
        return true;
    }

    /// <summary>Whether a command line opens on a <c>$</c> shell prompt — the one prompt marker
    /// that doesn't collide with markdown (<c>#</c> heading, <c>&gt;</c> blockquote).</summary>
    private static bool IsShellPrompt(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        return i < line.Length && line[i] == '$';
    }

    /// <summary>Whether a <c>key=value</c> line reports a failure, which is the one thing in a
    /// transcript worth finding without reading it.</summary>
    private static bool IsFailureStatus(string key, string value)
    {
        if (!IsStatusKey(key)) return false;

        var trimmed = value.Trim();
        if (trimmed.Length == 0) return false;
        // Numeric status: anything but 0 failed. Non-numeric: only the explicit failure words, so
        // `status=running` stays neutral.
        return int.TryParse(trimmed, out var code)
            ? code != 0
            : trimmed.Equals("error", StringComparison.OrdinalIgnoreCase)
              || trimmed.Equals("failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a <c>key=value</c> line's key names a process exit/return status — the
    /// one <c>key=value</c> shape distinctive enough to anchor the console format on its own.</summary>
    private static bool IsStatusKey(string key)
        => key.Equals("exit_code", StringComparison.OrdinalIgnoreCase)
            || key.Equals("exitcode", StringComparison.OrdinalIgnoreCase)
            || key.Equals("returncode", StringComparison.OrdinalIgnoreCase)
            || key.Equals("status", StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits into (content, lineEnding) pairs, preserving CRLF vs LF and whether the
    /// last line had a terminator — so concatenating the tokens rebuilds the input exactly.</summary>
    private static List<(string Line, string Ending)> SplitKeepingLineEndings(string text)
    {
        var result = new List<(string, string)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var hasCarriageReturn = i > start && text[i - 1] == '\r';
            var contentEnd = hasCarriageReturn ? i - 1 : i;
            result.Add((text[start..contentEnd], hasCarriageReturn ? "\r\n" : "\n"));
            start = i + 1;
        }
        if (start < text.Length) result.Add((text[start..], ""));
        return result;
    }

    private static bool TryFormatJson(string text, out IReadOnlyList<ResultToken> tokens)
    {
        tokens = Array.Empty<ResultToken>();

        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length < 2 || trimmed.Length > MaxStructuredInputLength) return false;
        // Only objects and arrays. A bare `42` or `"ok"` is valid JSON, but a tool that returns
        // one meant it as a plain answer, and quoting or recolouring it would be a lie about what
        // the tool sent.
        var opens = trimmed[0] is '{' or '[';
        if (!opens) return false;

        try
        {
            using var document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions
                {
                    // A trailing comma or a `// note` is exactly the sort of near-JSON a tool
                    // emits; accepting it here means it still gets indented rather than being
                    // dumped as one flat line.
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = MaxStructuredDepth,
                });

            var collected = new List<ResultToken>();
            WriteValue(document.RootElement, collected, depth: 0);
            tokens = collected;
            return collected.Count > 0;
        }
        catch (JsonException)
        {
            return false; // not JSON after all — plain text path
        }
    }

    private static void WriteValue(JsonElement element, List<ResultToken> output, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteContainer(element, output, depth, '{', '}', isObject: true);
                break;
            case JsonValueKind.Array:
                WriteContainer(element, output, depth, '[', ']', isObject: false);
                break;
            case JsonValueKind.String:
                // GetRawText keeps the original escaping, so a value containing a quote or a
                // newline still reads as the JSON the tool actually sent.
                output.Add(new ResultToken(element.GetRawText(), ResultTokenKind.StringLiteral));
                break;
            case JsonValueKind.Number:
                output.Add(new ResultToken(element.GetRawText(), ResultTokenKind.Number));
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                output.Add(new ResultToken(element.GetRawText(), ResultTokenKind.Keyword));
                break;
            default:
                output.Add(new ResultToken(element.GetRawText(), ResultTokenKind.Plain));
                break;
        }
    }

    private static void WriteContainer(
        JsonElement element, List<ResultToken> output, int depth, char open, char close, bool isObject)
    {
        // An empty container stays on one line: "{}" reads better than a brace, a blank line and
        // another brace, and it is what every formatter does.
        var isEmpty = isObject
            ? !element.EnumerateObject().MoveNext()
            : !element.EnumerateArray().MoveNext();
        if (isEmpty)
        {
            output.Add(new ResultToken($"{open}{close}", ResultTokenKind.Punctuation));
            return;
        }

        output.Add(new ResultToken(open.ToString(), ResultTokenKind.Punctuation));

        var first = true;
        if (isObject)
        {
            foreach (var property in element.EnumerateObject())
            {
                WriteSeparator(output, ref first, depth + 1);
                output.Add(new ResultToken(JsonEncodedName(property.Name), ResultTokenKind.Key));
                output.Add(new ResultToken(": ", ResultTokenKind.Punctuation));
                WriteValue(property.Value, output, depth + 1);
            }
        }
        else
        {
            foreach (var item in element.EnumerateArray())
            {
                WriteSeparator(output, ref first, depth + 1);
                WriteValue(item, output, depth + 1);
            }
        }

        output.Add(new ResultToken(NewLineIndent(depth), ResultTokenKind.Plain));
        output.Add(new ResultToken(close.ToString(), ResultTokenKind.Punctuation));
    }

    /// <summary>Emits the comma between entries and the newline + indent before each one. The
    /// comma belongs to the *previous* entry's line, which is why it is written here rather than
    /// after each value.</summary>
    private static void WriteSeparator(List<ResultToken> output, ref bool first, int depth)
    {
        if (!first) output.Add(new ResultToken(",", ResultTokenKind.Punctuation));
        first = false;
        output.Add(new ResultToken(NewLineIndent(depth), ResultTokenKind.Plain));
    }

    private static string NewLineIndent(int depth) => "\n" + new string(' ', depth * 2);

    /// <summary>The property name as it should read on screen: quoted, with any quote or
    /// backslash in the name itself escaped so the rendering stays unambiguous.</summary>
    private static string JsonEncodedName(string name)
        => "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal)
                      .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
