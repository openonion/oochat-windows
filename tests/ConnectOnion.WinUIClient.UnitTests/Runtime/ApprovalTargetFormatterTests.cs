using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

public sealed class ApprovalTargetFormatterTests
{
    [Fact]
    public void FilePath_ShowsLastSegment_KeepsFullPathForTooltip()
    {
        var t = ApprovalTargetFormatter.Extract("""{"path":"/home/user/notes/Project_Almond_weekly_meeting_notes.txt"}""");

        Assert.Equal(ApprovalTargetKind.File, t.Kind);
        Assert.Equal("Project_Almond_weekly_meeting_notes.txt", t.Target);
        Assert.Equal("/home/user/notes/Project_Almond_weekly_meeting_notes.txt", t.FullTarget);
        Assert.Equal("modify", t.OperationVerb);
        Assert.True(t.HasTarget);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("file_path")]
    [InlineData("filename")]
    [InlineData("target")]
    [InlineData("destination")]
    public void EveryFileFieldName_IsRecognized(string key)
    {
        var t = ApprovalTargetFormatter.Extract($$"""{"{{key}}":"report.pdf"}""");

        Assert.Equal(ApprovalTargetKind.File, t.Kind);
        Assert.Equal("report.pdf", t.Target);
    }

    [Fact]
    public void WindowsPath_IsSplitOnBackslashes()
    {
        var t = ApprovalTargetFormatter.Extract("""{"path":"C:\\Users\\me\\Documents\\budget.xlsx"}""");

        Assert.Equal("budget.xlsx", t.Target);
    }

    [Fact]
    public void Url_ShowsHost_KeepsFullUrlForTooltip()
    {
        var t = ApprovalTargetFormatter.Extract("""{"url":"https://api.example.com/v1/send?ref=abc123&x=1"}""");

        Assert.Equal(ApprovalTargetKind.Url, t.Kind);
        Assert.Equal("api.example.com", t.Target);
        Assert.Equal("https://api.example.com/v1/send?ref=abc123&x=1", t.FullTarget);
        Assert.Equal("reach", t.OperationVerb);
    }

    [Fact]
    public void Command_IsFlattenedToOneLine()
    {
        var t = ApprovalTargetFormatter.Extract("""{"command":"rm -rf /tmp/cache\nrm -rf /tmp/logs"}""");

        Assert.Equal(ApprovalTargetKind.Command, t.Kind);
        Assert.Equal("rm -rf /tmp/cache rm -rf /tmp/logs", t.Target);
        Assert.Equal("run the command", t.OperationVerb);
    }

    [Fact]
    public void Directory_IsRecognized()
    {
        var t = ApprovalTargetFormatter.Extract("""{"directory":"/var/www/html"}""");

        Assert.Equal(ApprovalTargetKind.Directory, t.Kind);
        Assert.Equal("html", t.Target);
        Assert.Equal("write to", t.OperationVerb);
    }

    [Fact]
    public void UrlWins_OverAPathInTheSamePayload()
    {
        // Specificity order: the URL identifies the action better than a path it also carries.
        var t = ApprovalTargetFormatter.Extract("""{"path":"cache.tmp","url":"https://example.com/x"}""");

        Assert.Equal(ApprovalTargetKind.Url, t.Kind);
        Assert.Equal("example.com", t.Target);
    }

    [Fact]
    public void FreeText_UsesNeutralTextKind()
    {
        var t = ApprovalTargetFormatter.Extract("""{"query":"quarterly revenue figures"}""");

        Assert.Equal(ApprovalTargetKind.Text, t.Kind);
        Assert.Equal("quarterly revenue figures", t.Target);
        Assert.Equal("send", t.OperationVerb);
    }

    [Fact]
    public void LongTarget_IsTruncatedWithEllipsis()
    {
        var longName = new string('a', 200) + ".txt";
        var t = ApprovalTargetFormatter.Extract($$"""{"filename":"{{longName}}"}""");

        Assert.True(t.Target.Length <= 72);
        Assert.EndsWith("…", t.Target);
        // The full value is still available for the tooltip.
        Assert.Equal(longName, t.FullTarget);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ broken json")]
    [InlineData("[]")]          // valid JSON, but not an object
    [InlineData("\"a string\"")] // valid JSON, but not an object
    [InlineData("{}")]           // object with no recognizable field
    [InlineData("""{"reason":"because"}""")] // unrelated field only
    public void NoExtractableTarget_FallsBackToEmpty(string? json)
    {
        var t = ApprovalTargetFormatter.Extract(json);

        Assert.Equal(ApprovalTargetKind.None, t.Kind);
        Assert.False(t.HasTarget);
        Assert.Equal("", t.Target);
        Assert.Equal("proceed", t.OperationVerb);
    }

    [Fact]
    public void InvalidJson_DoesNotThrow()
    {
        // The record has to survive the agent sending anything at all in place of arguments.
        var ex = Record.Exception(() => ApprovalTargetFormatter.Extract("{\"path\": \"a.txt\", unterminated"));
        Assert.Null(ex);
    }
}
