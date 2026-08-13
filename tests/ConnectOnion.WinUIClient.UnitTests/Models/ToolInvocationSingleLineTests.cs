using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

/// <summary>
/// The collapsed step row is a fixed one-scan-height slot, and the value it shows comes straight
/// from the agent. <c>TextWrapping="NoWrap"</c> in the XAML does not make that true: it suppresses
/// automatic wrapping, not literal newlines. A tool whose argument is a whole document —
/// <c>write_plan</c> hands over the entire plan — therefore unfolded across the transcript the
/// moment its step was <b>collapsed</b>, which is the only state that renders this row.
/// </summary>
public sealed class ToolInvocationSingleLineTests
{
    [Fact]
    public void MultiLineArgument_CollapsesToOneLine()
    {
        var plan = "# Audit\n\n## Objective\nCheck all agents.\n\n| a | b |\n|---|---|\n| 1 | 2 |";
        var invocation = new ToolInvocation(ToolInvocationKind.Text, "Plan", plan);

        var line = invocation.SingleLineText;

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Equal("# Audit ## Objective Check all agents. | a | b | |---|---| | 1 | 2 |", line);
    }

    [Fact]
    public void RunsOfWhitespace_BecomeASingleSpace()
    {
        var invocation = new ToolInvocation(
            ToolInvocationKind.Text, "Plan", "one\n\n\n   two\t\t three");

        Assert.Equal("one two three", invocation.SingleLineText);
    }

    [Fact]
    public void LeadingAndTrailingWhitespace_IsDropped()
    {
        var invocation = new ToolInvocation(ToolInvocationKind.Text, "Plan", "\n\n  hello  \n\n");

        Assert.Equal("hello", invocation.SingleLineText);
    }

    /// <summary>A whole document must not be handed to a TextBlock that will only ever show one
    /// ellipsized line of it.</summary>
    [Fact]
    public void VeryLongArgument_IsTruncatedWithAnEllipsis()
    {
        var invocation = new ToolInvocation(
            ToolInvocationKind.Text, "Plan", new string('x', 5000));

        var line = invocation.SingleLineText;

        Assert.True(line.Length < 400, $"Collapsed preview was {line.Length} chars.");
        Assert.EndsWith("…", line, StringComparison.Ordinal);
    }

    /// <summary>Flattening is not truncation: a short multi-line value loses its newlines but must
    /// not claim to have been cut short.</summary>
    [Fact]
    public void ShortFlattenedValue_GetsNoEllipsis()
    {
        var invocation = new ToolInvocation(ToolInvocationKind.Text, "Plan", "line one\nline two");

        Assert.Equal("line one line two", invocation.SingleLineText);
    }

    [Fact]
    public void EmptyOrWhitespaceOnly_IsEmpty()
    {
        Assert.Equal("", new ToolInvocation(ToolInvocationKind.Text, "l", "").SingleLineText);
        Assert.Equal("", new ToolInvocation(ToolInvocationKind.Text, "l", "  \n\t ").SingleLineText);
    }

    /// <summary>The expanded block shows the value in full; only the collapsed row is flattened.</summary>
    [Fact]
    public void ExpandedFormsStayVerbatim()
    {
        const string raw = "line one\nline two";
        var invocation = new ToolInvocation(ToolInvocationKind.Command, "Command", raw, Prefix: "$");

        Assert.Equal(raw, invocation.Text);
        Assert.Equal("$ " + raw, invocation.DisplayText);
    }

    /// <summary>write_plan returns the plan it just wrote — a markdown document, not program
    /// output — so the step body renders it as prose rather than as literal '#' and '|'. Listed by
    /// exact name: `exit_plan_and_implement` also carries "plan" and its result is a status report.</summary>
    [Theory]
    [InlineData("write_plan", true)]
    [InlineData("update_plan", true)]
    [InlineData("load_guide", true)]
    [InlineData("remote_load_guide", true)]
    [InlineData("exit_plan_and_implement", false)]
    [InlineData("bash", false)]
    [InlineData("read_file", false)]
    public void MarkdownResultTools_AreAnExplicitOptIn(string tool, bool expected)
        => Assert.Equal(expected, ToolInvocations.ProducesMarkdown(tool));
}
