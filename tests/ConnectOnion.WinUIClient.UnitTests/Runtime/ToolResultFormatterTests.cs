using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

public sealed class ToolResultFormatterTests
{
    /// <summary>The invariant the whole design rests on: formatting only ever changes how the
    /// text is *split and coloured*, never — for a plain result — what it says.</summary>
    private static string Rendered(FormattedToolResult result)
        => string.Concat(result.Tokens.Select(t => t.Text));

    [Theory]
    [InlineData("File written successfully.")]
    [InlineData("line one\nline two\n  indented three")]
    [InlineData("| col a | col b |\n|-------|-------|\n| 1     | 2     |")]
    [InlineData("not json { but has braces")]
    [InlineData("42")]
    [InlineData("\"a bare string\"")]
    [InlineData("A sentence where x=1 appears once, which is not a transcript.")]
    public void PlainResult_IsPassedThroughUntouched(string text)
    {
        var result = ToolResultFormatter.Format(text);

        Assert.Equal(ResultFormat.PlainText, result.Format);
        // A tool that printed a table or a diff chose its own layout; reflowing it would ruin it.
        Assert.Equal(text, Rendered(result));
        Assert.All(result.Tokens, t => Assert.Equal(ResultTokenKind.Plain, t.Kind));
    }

    // ---- Console transcripts: coloured in place, never re-laid-out ----

    /// <summary>The shape the shell-running tools actually produce.</summary>
    private const string Transcript =
        "timestamp=2026-07-18T15:16:43\n" +
        "\n" +
        "$ hostname\n" +
        "exit_code=0\n" +
        "\n" +
        "stdout:\n" +
        "VM-0-8-ubuntu\n";

    [Fact]
    public void ConsoleTranscript_IsRecognized_AndNotOneCharacterIsChanged()
    {
        var result = ToolResultFormatter.Format(Transcript);

        Assert.Equal(ResultFormat.Console, result.Format);
        // The whole point of this path: aligned output survives byte for byte.
        Assert.Equal(Transcript, Rendered(result));
    }

    [Fact]
    public void ConsoleTranscript_TypesItsStructuralLines()
    {
        var result = ToolResultFormatter.Format(Transcript);

        Assert.Contains(result.Tokens, t => t.Kind == ResultTokenKind.Command && t.Text == "$ hostname");
        Assert.Contains(result.Tokens, t => t.Kind == ResultTokenKind.Label && t.Text == "stdout:");
        Assert.Contains(result.Tokens, t => t.Kind == ResultTokenKind.Key && t.Text == "timestamp");
        // Plain output lines stay plain — only structure is coloured.
        Assert.Contains(result.Tokens, t => t.Kind == ResultTokenKind.Plain && t.Text == "VM-0-8-ubuntu");
    }

    [Theory]
    [InlineData("exit_code=0", false)]
    [InlineData("exit_code=1", true)]
    [InlineData("returncode=127", true)]
    [InlineData("status=failed", true)]
    [InlineData("status=running", false)]
    public void ExitStatus_IsFlaggedOnlyWhenItActuallyFailed(string line, bool expectFailure)
    {
        // A failed step has to be findable in a long transcript without reading it; a successful
        // one must not shout.
        var result = ToolResultFormatter.Format("$ run\n" + line + "\nstdout:\n");

        Assert.Equal(expectFailure, result.Tokens.Any(t => t.Kind == ResultTokenKind.Failure));
    }

    [Fact]
    public void AlignedColumns_SurviveExactly()
    {
        // A `ps` listing: the alignment *is* the information.
        const string listing =
            "$ ps aux\n" +
            "  PID  PPID STAT ELAPSED COMMAND\n" +
            "2241075   1 Ssl 04:51:54 python agent.py\n" +
            "exit_code=0\n";

        var result = ToolResultFormatter.Format(listing);

        Assert.Equal(ResultFormat.Console, result.Format);
        Assert.Equal(listing, Rendered(result));
    }

    [Theory]
    [InlineData("a\r\nb\r\n")]
    [InlineData("$ one\r\nexit_code=0\r\nstdout:\r\n")]
    public void LineEndings_AreRebuiltExactly(string text)
    {
        // CRLF vs LF, and whether the last line was terminated, all have to round-trip.
        Assert.Equal(text, Rendered(ToolResultFormatter.Format(text)));
    }

    [Fact]
    public void OneStrayKeyValueLine_DoesNotMakeProseATranscript()
    {
        // The bar is two structural lines; a single match in prose must not recolour the text.
        var result = ToolResultFormatter.Format("The config sets retries=3 and that is all.");

        Assert.Equal(ResultFormat.PlainText, result.Format);
    }

    [Theory]
    // Two `key=value`-shaped lines with no console anchor: URL query params from a fetched page…
    [InlineData("utm_source=google\nutm_medium=cpc")]
    // …form-encoded data, a filename that happens to carry an '='…
    [InlineData("ref=abc123\nfile=export-2026==")]
    // …a config snippet quoted out of a web page…
    [InlineData("timeout=30\nretries=3")]
    // …and markdown headings/blockquotes, which share the `#`/`>` command markers.
    [InlineData("# Getting started\n> Note: read this first")]
    public void KeyValueOrMarkdownLines_WithoutAConsoleAnchor_StayPlain(string text)
    {
        // The whole point of the fix: a fetched web page or a file listing must not be recoloured
        // as a shell transcript just because two of its lines look like `key=value` or markdown.
        var result = ToolResultFormatter.Format(text);

        Assert.Equal(ResultFormat.PlainText, result.Format);
        Assert.Equal(text, Rendered(result));
        Assert.All(result.Tokens, t => Assert.Equal(ResultTokenKind.Plain, t.Kind));
    }

    [Theory]
    // A `$` prompt anchors it; the weak `key=value` line rides along and gets coloured.
    [InlineData("$ curl https://example.com?ref=1\nref=1")]
    // A status line is itself an anchor, so a lone weak line beside it is enough.
    [InlineData("downloaded=report.pdf\nexit_code=0")]
    // A stdout: label anchors it.
    [InlineData("stdout:\nutm_source=google")]
    public void OneConsoleAnchorPlusAWeakLine_IsRecognizedAsConsole(string text)
    {
        var result = ToolResultFormatter.Format(text);

        Assert.Equal(ResultFormat.Console, result.Format);
        Assert.Equal(text, Rendered(result));
    }

    [Fact]
    public void Json_WinsOverConsole_WhenTheResultIsBoth()
    {
        // A JSON body whose string values contain "$ cmd" lines is still JSON.
        var result = ToolResultFormatter.Format("""{"log":"$ a\nexit_code=0","n":1}""");

        Assert.Equal(ResultFormat.Json, result.Format);
    }

    [Fact]
    public void MinifiedJson_IsReindented()
    {
        var result = ToolResultFormatter.Format("""{"name":"a.txt","size":12}""");

        Assert.True(result.IsStructured);
        Assert.Equal(
            "{\n  \"name\": \"a.txt\",\n  \"size\": 12\n}",
            Rendered(result));
    }

    [Fact]
    public void JsonValues_AreTypedForColouring()
    {
        var result = ToolResultFormatter.Format("""{"k":"v","n":1,"ok":true,"none":null}""");

        var kinds = result.Tokens
            .Where(t => t.Kind != ResultTokenKind.Plain && t.Kind != ResultTokenKind.Punctuation)
            .Select(t => (t.Text, t.Kind))
            .ToArray();

        Assert.Equal(
            new[]
            {
                ("\"k\"", ResultTokenKind.Key),
                ("\"v\"", ResultTokenKind.StringLiteral),
                ("\"n\"", ResultTokenKind.Key),
                ("1", ResultTokenKind.Number),
                ("\"ok\"", ResultTokenKind.Key),
                ("true", ResultTokenKind.Keyword),
                ("\"none\"", ResultTokenKind.Key),
                ("null", ResultTokenKind.Keyword),
            },
            kinds);
    }

    [Fact]
    public void NestedStructures_IndentByDepth()
    {
        var result = ToolResultFormatter.Format("""{"outer":{"inner":[1,2]}}""");

        Assert.Equal(
            "{\n  \"outer\": {\n    \"inner\": [\n      1,\n      2\n    ]\n  }\n}",
            Rendered(result));
    }

    [Fact]
    public void EmptyContainers_StayOnOneLine()
    {
        // "{}" reads better than a brace, a blank line, and another brace.
        Assert.Equal("{\n  \"a\": {},\n  \"b\": []\n}",
            Rendered(ToolResultFormatter.Format("""{"a":{},"b":[]}""")));
    }

    [Fact]
    public void TopLevelArray_IsStructured()
    {
        var result = ToolResultFormatter.Format("""["a","b"]""");

        Assert.True(result.IsStructured);
        Assert.Equal("[\n  \"a\",\n  \"b\"\n]", Rendered(result));
    }

    [Fact]
    public void NearJson_WithTrailingCommaOrComment_StillFormats()
    {
        // Exactly the sort of almost-JSON a tool emits; worth indenting rather than dumping flat.
        Assert.True(ToolResultFormatter.Format("""{"a":1,}""").IsStructured);
        Assert.True(ToolResultFormatter.Format("{\"a\":1 // note\n}").IsStructured);
    }

    [Fact]
    public void MalformedJson_FallsBackToPlainText_WithoutLosingCharacters()
    {
        const string broken = """{"unterminated": "value""";

        var result = ToolResultFormatter.Format(broken);

        Assert.False(result.IsStructured);
        Assert.Equal(broken, Rendered(result));
    }

    [Fact]
    public void StringValues_KeepTheirOriginalEscaping()
    {
        // Round-tripping through GetRawText is what keeps a quote or newline inside a value
        // readable as the JSON the tool actually sent.
        var result = ToolResultFormatter.Format("""{"s":"line\nbreak \"quoted\""}""");

        Assert.Contains(result.Tokens, t =>
            t.Kind == ResultTokenKind.StringLiteral && t.Text == "\"line\\nbreak \\\"quoted\\\"\"");
    }

    [Fact]
    public void KeyContainingAQuote_IsEscapedSoTheRenderingStaysUnambiguous()
    {
        var result = ToolResultFormatter.Format("""{"a\"b":1}""");

        Assert.Contains(result.Tokens, t => t.Kind == ResultTokenKind.Key && t.Text == "\"a\\\"b\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyInput_ProducesNoTokens(string? text)
    {
        var result = ToolResultFormatter.Format(text);

        Assert.Empty(result.Tokens);
        Assert.False(result.IsStructured);
    }

    [Fact]
    public void DeeplyNestedJson_FallsBackToPlainRatherThanRecursingUnbounded()
    {
        // 40 levels, past the 32-deep guard.
        var deep = new string('[', 40) + new string(']', 40);

        var result = ToolResultFormatter.Format(deep);

        Assert.False(result.IsStructured);
        Assert.Equal(deep, Rendered(result));
    }
}
