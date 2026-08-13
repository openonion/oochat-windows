using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

/// <summary>
/// The tool-name → invocation classification behind the timeline's "what was this asked to do" line.
/// Table-driven because the table <i>is</i> the feature: getting a family wrong shows a CSS selector
/// where the agent wrote a sentence, or nothing at all where a command ran.
/// </summary>
public sealed class ToolInvocationsTests
{
    [Theory]
    // The case the feature exists for: a shell step used to show its name and its output and never
    // the command in between.
    [InlineData("bash", """{"command":"npm test -- --watch=false"}""", "npm test -- --watch=false", "Command", "$")]
    [InlineData("run_command", """{"command":"ls -la"}""", "ls -la", "Command", "$")]
    // A remote_ / fs. prefixed variant of the same capability reaches the family by keyword.
    [InlineData("remote_run_command", """{"command":"whoami"}""", "whoami", "Command", "$")]
    [InlineData("grep", """{"pattern":"TODO"}""", "TODO", "Pattern", "")]
    [InlineData("glob", """{"pattern":"**/*.cs"}""", "**/*.cs", "Pattern", "")]
    [InlineData("read_file", """{"path":"src/App.xaml.cs"}""", "src/App.xaml.cs", "Path", "")]
    [InlineData("write_file", """{"file_path":"notes.md"}""", "notes.md", "Path", "")]
    [InlineData("go_to", """{"url":"https://example.com/pricing"}""", "https://example.com/pricing", "URL", "")]
    public void Read_MapsEachFamilyToItsArgument(
        string tool, string args, string expectedText, string expectedLabel, string expectedPrefix)
    {
        var invocation = ToolInvocations.Read(tool, args);

        Assert.True(invocation.HasValue);
        Assert.Equal(expectedText, invocation.Text);
        Assert.Equal(expectedLabel, invocation.Label);
        Assert.Equal(expectedPrefix, invocation.Prefix);
    }

    [Fact]
    public void Read_BrowserClick_PrefersTheHumanDescriptionOverTheSelector()
    {
        // The whole reason browsers are a family: a generic probe would print the selector, and a
        // selector tells a reader nothing the agent's own description does not say better.
        var invocation = ToolInvocations.Read(
            "click", """{"selector":"div.card:nth-child(3) > button","description":"the Sign in button"}""");

        Assert.Equal("the Sign in button", invocation.Text);
        Assert.Equal(ToolInvocationKind.Text, invocation.Kind);
    }

    [Fact]
    public void Read_BrowserClick_FallsBackToTheSelectorWhenThereIsNoDescription()
    {
        var invocation = ToolInvocations.Read("click", """{"selector":"#submit"}""");

        Assert.Equal("#submit", invocation.Text);
    }

    [Fact]
    public void Read_Search_CarriesItsScopeAsASecondLine()
    {
        // "TODO" alone does not say whether one file or the whole tree was swept.
        var invocation = ToolInvocations.Read("grep", """{"pattern":"TODO","path":"src/"}""");

        Assert.Equal("TODO", invocation.Text);
        Assert.True(invocation.HasSecondary);
        Assert.Equal("in src/", invocation.Secondary);
    }

    [Fact]
    public void Read_DelegatedTask_LeadsWithTheDescriptionAndKeepsThePrompt()
    {
        var invocation = ToolInvocations.Read(
            "call_omo_agent",
            """{"description":"Audit the login flow","prompt":"Read auth.ts and report every place a token is logged."}""");

        Assert.Equal(ToolInvocationKind.Task, invocation.Kind);
        Assert.Equal("Audit the login flow", invocation.Text);
        Assert.Equal("Read auth.ts and report every place a token is logged.", invocation.Secondary);
    }

    [Fact]
    public void Read_DelegatedTask_DoesNotRepeatThePromptAsItsOwnSecondLine()
    {
        // With no description the prompt becomes the headline; printing it twice would just be a
        // taller card saying the same thing.
        var invocation = ToolInvocations.Read("call_omo_agent", """{"prompt":"Summarize the diff"}""");

        Assert.Equal("Summarize the diff", invocation.Text);
        Assert.False(invocation.HasSecondary);
    }

    [Fact]
    public void Read_UnknownTool_StillProbesForSomethingRecognizable()
    {
        // A custom tool nobody listed should not have to be listed to read correctly.
        var invocation = ToolInvocations.Read("acme_fetch_thing", """{"url":"https://acme.test/a"}""");

        Assert.Equal(ToolInvocationKind.Url, invocation.Kind);
        Assert.Equal("https://acme.test/a", invocation.Text);
    }

    [Theory]
    [InlineData("bash", "{}")]
    [InlineData("bash", null)]
    [InlineData("bash", "")]
    // Not JSON at all — SanitizeJson falls back to scrubbed text when a tool sends something else.
    [InlineData("bash", "just some text")]
    // A JSON array is valid JSON but has no named fields to attribute.
    [InlineData("bash", "[1,2,3]")]
    // A non-string in a command slot is a malformed frame; rendering {"a":1} under a "Command"
    // heading would invent a fact.
    [InlineData("bash", """{"command":{"a":1}}""")]
    [InlineData("bash", """{"command":"   "}""")]
    public void Read_NothingRenderable_ReturnsNone(string tool, string? args)
    {
        var invocation = ToolInvocations.Read(tool, args);

        Assert.Equal(ToolInvocationKind.None, invocation.Kind);
        Assert.False(invocation.HasValue);
    }

    [Fact]
    public void Read_TrimsSurroundingWhitespace()
    {
        Assert.Equal("ls -la", ToolInvocations.Read("bash", """{"command":"  ls -la\n"}""").Text);
    }

    [Fact]
    public void Read_LoadGuide_NamesTheGuideItLoaded()
    {
        var invocation = ToolInvocations.Read("load_guide", """{"guide_path":".co/guides/deploy.md"}""");

        Assert.Equal(ToolInvocationKind.Path, invocation.Kind);
        Assert.Equal(".co/guides/deploy.md", invocation.Text);
    }

    [Theory]
    [InlineData("load_guide", true)]
    [InlineData("remote_load_guide", true)]
    [InlineData("read_guide", true)]
    // Everything else keeps the monospace log: a shell transcript or a stack trace depends on its
    // exact whitespace, and a prose renderer destroys it.
    [InlineData("bash", false)]
    [InlineData("read_file", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ProducesMarkdown_IsAnOptIn_NotAGuessFromContent(string? tool, bool expected)
    {
        Assert.Equal(expected, ToolInvocations.ProducesMarkdown(tool));
    }
}

/// <summary>
/// The step model's side of the same feature: the invocation is derived from the persisted
/// arguments, so it must appear on a step rebuilt from history and must stand down the row's target
/// digest when it does.
/// </summary>
public sealed class ToolStepInvocationTests
{
    [Fact]
    public void Invocation_IsDerivedFromPersistedArguments()
    {
        // Exactly the shape a step deserialized from the messages table has: no projector ever ran
        // on it, only ToolName and Arguments came back.
        var step = new ToolStepViewModel { ToolName = "bash", Arguments = """{"command":"git status"}""" };

        Assert.True(step.HasInvocation);
        Assert.Equal("git status", step.InvocationText);
        Assert.Equal("$", step.InvocationPrefix);
        Assert.Equal("$ git status", step.InvocationDisplayText);
        Assert.Equal("Command", step.InvocationLabel);
    }

    [Fact]
    public void OpenAction_IsLimitedToHttpUrls()
    {
        var web = new ToolStepViewModel
        {
            ToolName = "go_to",
            Arguments = """{"url":"https://example.test/path"}""",
        };
        var file = new ToolStepViewModel
        {
            ToolName = "read_file",
            Arguments = """{"path":"C:\\temp\\notes.txt"}""",
        };

        Assert.True(web.CanOpenInvocation);
        Assert.False(file.CanOpenInvocation);
    }

    [Fact]
    public void InlineInvocation_StandsDownOnceTheStepIsExpanded()
    {
        // The expanded block prints the same value in full; showing both put the command on screen
        // twice, which is the redundancy the single Result/Error block already exists to avoid.
        var step = new ToolStepViewModel { ToolName = "bash", Arguments = """{"command":"git status"}""" };
        Assert.True(step.ShowInlineInvocation);

        step.IsExpanded = true;
        Assert.False(step.ShowInlineInvocation);
        Assert.True(step.HasInvocation);
    }

    [Fact]
    public void Invocation_RecomputesWhenArgumentsChange()
    {
        var step = new ToolStepViewModel { ToolName = "bash", Arguments = """{"command":"first"}""" };
        Assert.Equal("first", step.InvocationText);

        // The memo must not outlive the value it was computed from.
        step.Arguments = """{"command":"second"}""";
        Assert.Equal("second", step.InvocationText);
    }

    [Fact]
    public void DisplayLabel_DropsTheTargetDigestOnceTheInvocationCarriesIt()
    {
        var step = new ToolStepViewModel
        {
            ToolName = "grep",
            DisplayName = "Search files",
            DisplayTarget = "Search: TODO",
            Arguments = """{"pattern":"TODO"}""",
        };

        Assert.False(step.ShowDisplayTarget);
        Assert.Equal("Search files", step.DisplayLabel);
    }

    [Fact]
    public void DisplayLabel_KeepsTheTargetDigestWhenThereIsNoInvocation()
    {
        var step = new ToolStepViewModel
        {
            ToolName = "mystery_tool",
            DisplayName = "Mystery Tool",
            DisplayTarget = "example.com",
            Arguments = "{}",
        };

        Assert.True(step.ShowDisplayTarget);
        Assert.Equal("Mystery Tool · example.com", step.DisplayLabel);
    }

    [Fact]
    public void GuideResult_RendersAsMarkdownRatherThanAsALog()
    {
        var step = new ToolStepViewModel
        {
            ToolName = "load_guide",
            Arguments = """{"guide_path":".co/guides/deploy.md"}""",
            Result = "# Deploying\n\n1. Build\n2. Ship",
        };

        Assert.True(step.RendersDetailAsMarkdown);
        Assert.False(step.RendersDetailAsLog);
    }

    [Fact]
    public void FailedGuideStep_FallsBackToTheLog()
    {
        // An error is a diagnostic whose exact characters matter; a markdown renderer would eat
        // the underscores and turn a leading '#' into a heading.
        var step = new ToolStepViewModel
        {
            ToolName = "load_guide",
            Arguments = "{}",
            Error = "# FileNotFoundError: .co/guides/missing.md",
        };

        Assert.False(step.RendersDetailAsMarkdown);
        Assert.True(step.RendersDetailAsLog);
    }

    [Fact]
    public void OrdinaryToolResult_StaysInTheLog()
    {
        var step = new ToolStepViewModel { ToolName = "bash", Arguments = "{}", Result = "# not a heading" };

        Assert.False(step.RendersDetailAsMarkdown);
        Assert.True(step.RendersDetailAsLog);
    }

    [Fact]
    public void StepWithNoOutput_RendersNeitherBlock()
    {
        var step = new ToolStepViewModel { ToolName = "load_guide", Arguments = "{}" };

        Assert.False(step.RendersDetailAsMarkdown);
        Assert.False(step.RendersDetailAsLog);
    }

    [Fact]
    public void Invocation_RendersTheRedactedArguments()
    {
        // Arguments reach the model already scrubbed by ToolActivityProjector.SanitizeJson, so the
        // invocation line cannot print a secret the timeline had already hidden.
        var sanitized = ConnectOnion.WinUIClient.Services.Runtime.ToolActivityProjector.SanitizeJson(
            """{"command":"curl -H 'x' https://api.test","api_key":"sk-live-123"}""");
        var step = new ToolStepViewModel { ToolName = "bash", Arguments = sanitized };

        Assert.DoesNotContain("sk-live-123", step.InvocationText, StringComparison.Ordinal);
        Assert.Contains("curl", step.InvocationText, StringComparison.Ordinal);
    }
}
