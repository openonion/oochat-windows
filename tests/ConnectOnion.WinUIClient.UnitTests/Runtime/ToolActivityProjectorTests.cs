using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

public sealed class ToolActivityProjectorTests
{
    [Fact]
    public void ApplyCall_ThenSuccessfulResult_TransitionsRunningToSuccess()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        var step = projector.ApplyCall(Event("tool_call", "{\"tool_id\":\"1\",\"name\":\"read_file\",\"args\":{}}"));

        var result = projector.ApplyResult(Event("tool_result", "{\"tool_id\":\"1\",\"status\":\"success\",\"result\":\"contents\"}"));
        projector.Complete();

        Assert.Same(step, result);
        Assert.Equal(ToolStepStatus.Success, step.Status);
        Assert.Equal(ToolActivityStatus.Success, activity.Status);
    }

    [Fact]
    public void ApplyResult_FailureText_MarksStepFailedAndActivityPartialSuccess()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        projector.ApplyCall(Event("tool_call", "{\"tool_id\":\"1\",\"name\":\"open_browser\",\"args\":{}}"));

        var step = projector.ApplyResult(Event("tool_result", "{\"tool_id\":\"1\",\"status\":\"error\",\"error\":\"connection refused\"}"));
        projector.Complete();

        Assert.Equal(ToolStepStatus.Failed, step!.Status);
        Assert.Equal("Connection refused", step.Summary);
        Assert.Equal(ToolActivityStatus.PartialSuccess, activity.Status);
    }

    [Fact]
    public void Complete_Cancelled_CancelsRunningSteps()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        var step = projector.ApplyCall(Event("tool_call", "{\"tool_id\":\"1\",\"name\":\"search_web\",\"args\":{}}"));

        projector.Complete(ToolActivityStatus.Cancelled);

        Assert.Equal(ToolStepStatus.Cancelled, step.Status);
        Assert.Equal(ToolActivityStatus.Cancelled, activity.Status);
    }

    [Fact]
    public void CompleteOptimistically_FreezesRunningStepsAsSuccessful()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        var step = projector.ApplyCall(
            Event("tool_call", "{\"tool_id\":\"1\",\"name\":\"search_web\",\"args\":{}}"));

        projector.CompleteOptimistically();

        Assert.Equal(ToolStepStatus.Success, step.Status);
        Assert.Equal(ToolActivityStatus.Success, activity.Status);
        Assert.True(activity.IsTerminal);
        Assert.Contains("Completed", activity.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyCall_DuplicateCorrelationId_ReturnsExistingStep()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        var frame = Event("tool_call", "{\"tool_id\":\"same\",\"name\":\"read_file\",\"args\":{}} ");

        var first = projector.ApplyCall(frame);
        var duplicate = projector.ApplyCall(frame);

        Assert.Same(first, duplicate);
        Assert.Single(activity.Steps);
    }

    [Fact]
    public void ApplyResult_MissingCorrelationId_DoesNotGuessByToolName()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        projector.ApplyCall(Event("tool_call", "{\"tool_id\":\"1\",\"name\":\"read_file\",\"args\":{}}"));

        var result = projector.ApplyResult(Event("tool_result", "{\"name\":\"read_file\",\"result\":\"contents\"}"));

        Assert.Null(result);
        Assert.Equal(ToolStepStatus.Running, activity.Steps[0].Status);
    }

    [Fact]
    public void ApplyCall_SensitiveArguments_RedactsSecretsRecursively()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);

        var step = projector.ApplyCall(Event("tool_call", "{\"tool_id\":\"1\",\"name\":\"send_email\",\"args\":{\"token\":\"secret-value\",\"nested\":{\"password\":\"password-value\"}}}"));

        Assert.DoesNotContain("secret-value", step.Arguments);
        Assert.DoesNotContain("password-value", step.Arguments);
        Assert.Contains("[hidden]", step.Arguments);
        Assert.True(step.IsHighRisk);
    }

    [Fact]
    public void ApplyResult_HugeResult_IsCappedBeforeItEntersTheMessageList()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        projector.ApplyCall(Event("tool_call", "{\"tool_id\":\"1\",\"name\":\"get_text\",\"args\":{}}"));

        // A page scrape or a file read is routinely this big, and the step is retained for the
        // whole conversation (message list, cache, and the persisted event_args row).
        var huge = new string('x', 400_000);
        var step = projector.ApplyResult(
            Event("tool_result", $"{{\"tool_id\":\"1\",\"status\":\"success\",\"result\":\"{huge}\"}}"));

        Assert.NotNull(step!.Result);
        Assert.True(step.Result!.Length < 9_000, $"result retained {step.Result.Length} chars");
        Assert.EndsWith("… (truncated)", step.Result);
        Assert.Equal(ToolStepStatus.Success, step.Status);
    }

    [Theory]
    // The real report: a successful Bing navigation whose redirect tracking id happens to
    // contain "403". Every word a user can read says success, so an amber card is unexplainable.
    [InlineData("Navigated to https://www.bing.com/search?q=UNSW+ranking&rdr=1&rdrig=750A8A63BA3A403A8733EAD7629913CE")]
    // Digits that are part of a longer number are not status codes either.
    [InlineData("Downloaded 4040 rows")]
    [InlineData("Navigated to https://example.com/item/14032")]
    public void ApplyResult_DigitsEmbeddedInUrlsOrNumbers_StaysSuccess(string result)
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        projector.ApplyCall(Event("tool_call", "{\"tool_id\":\"1\",\"name\":\"navigate\",\"args\":{}}"));

        var step = projector.ApplyResult(
            Event("tool_result", $"{{\"tool_id\":\"1\",\"status\":\"success\",\"result\":\"{result}\"}}"));
        projector.Complete();

        Assert.Equal(ToolStepStatus.Success, step!.Status);
        Assert.Equal(ToolActivityStatus.Success, activity.Status);
    }

    [Theory]
    // A real status code still has to register — the boundary fix must not disarm the check.
    // Note none of these say "failed"/"error": those words would make it a Failure instead,
    // which is the documented precedence, not a 404 being ignored.
    [InlineData("Server returned 404 Not Found")]
    [InlineData("GET /missing -> 404")]
    [InlineData("Blocked with status 403")]
    public void ApplyResult_GenuineHttpStatusCode_IsStillAWarning(string result)
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        projector.ApplyCall(Event("tool_call", "{\"tool_id\":\"1\",\"name\":\"navigate\",\"args\":{}}"));

        var step = projector.ApplyResult(
            Event("tool_result", $"{{\"tool_id\":\"1\",\"status\":\"success\",\"result\":\"{result}\"}}"));

        Assert.Equal(ToolStepStatus.Warning, step!.Status);
    }

    [Fact]
    public void ApplyResult_403WithoutFailureWording_SummarisesAsAccessDenied()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        projector.ApplyCall(Event("tool_call", "{\"tool_id\":\"1\",\"name\":\"navigate\",\"args\":{}}"));

        var step = projector.ApplyResult(
            Event("tool_result", "{\"tool_id\":\"1\",\"status\":\"success\",\"result\":\"Blocked with status 403\"}"));

        Assert.Equal("Page access denied", step!.Summary);
    }

    /// <summary>
    /// A tool that hands back content must never be judged by the words inside that content.
    /// Reading a system prompt that explains error handling, a log file, or any source file with a
    /// `catch` block used to paint the step red, and a file mentioning a 404 painted it amber —
    /// the words belong to the document, not to the tool.
    /// </summary>
    [Theory]
    [InlineData("read_file", "# System Prompt\\n\\nOn failure, retry once. Log any exception.")]
    [InlineData("read", "try { x() } catch (Exception e) { log(\\\"request failed\\\") }")]
    [InlineData("remote_read_file", "timeout = 30  # seconds before the request times out")]
    [InlineData("cat", "HTTP status reference: 403 Forbidden, 404 Not Found")]
    [InlineData("grep", "server.py:42:    raise RuntimeError(\\\"connection refused\\\")")]
    [InlineData("load_guide", "## Troubleshooting\\n\\nIf the build failed, check the SDK version.")]
    [InlineData("search_files", "notes.md: the deploy failed last Tuesday")]
    public void ApplyResult_ContentReturningTool_IsSuccessRegardlessOfWordsInTheContent(
        string toolName,
        string content)
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        projector.ApplyCall(Event("tool_call", $"{{\"tool_id\":\"1\",\"name\":\"{toolName}\",\"args\":{{}}}}"));

        var step = projector.ApplyResult(
            Event("tool_result", $"{{\"tool_id\":\"1\",\"result\":\"{content}\"}}"));
        projector.Complete();

        Assert.Equal(ToolStepStatus.Success, step!.Status);
        Assert.Equal(ToolActivityStatus.Success, activity.Status);
    }

    /// <summary>The exemption is about *inferring* failure from prose, not about ignoring the
    /// host. A read the host itself reports as an error is still a failure.</summary>
    [Fact]
    public void ApplyResult_ContentReturningTool_StillFailsWhenTheHostSaysSo()
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        projector.ApplyCall(Event("tool_call", "{\"tool_id\":\"1\",\"name\":\"read_file\",\"args\":{}}"));

        var step = projector.ApplyResult(
            Event("tool_result", "{\"tool_id\":\"1\",\"status\":\"error\",\"error\":\"No such file or directory\"}"));

        Assert.Equal(ToolStepStatus.Failed, step!.Status);
    }

    /// <summary>Action tools keep the prose heuristics — that is where a result really is the tool
    /// describing what happened to it.</summary>
    [Theory]
    [InlineData("bash", "bash: ./deploy.sh: command failed")]
    [InlineData("navigate", "navigation timeout after 30s")]
    [InlineData("write_file", "write failed: disk full")]
    public void ApplyResult_ActionTool_StillInfersFailureFromItsResultText(string toolName, string result)
    {
        var activity = new ToolActivityViewModel();
        var projector = new ToolActivityProjector(activity);
        projector.ApplyCall(Event("tool_call", $"{{\"tool_id\":\"1\",\"name\":\"{toolName}\",\"args\":{{}}}}"));

        var step = projector.ApplyResult(
            Event("tool_result", $"{{\"tool_id\":\"1\",\"result\":\"{result}\"}}"));

        Assert.Equal(ToolStepStatus.Failed, step!.Status);
    }

    /// <summary>The keyword pass is boundary-anchored so it covers prefixed variants without
    /// swallowing names that merely contain the letters — "thread" contains "read".</summary>
    [Theory]
    [InlineData("read", true)]
    [InlineData("read_file", true)]
    [InlineData("remote_read_file", true)]
    [InlineData("fs.readFile", true)]
    [InlineData("grep", true)]
    [InlineData("load_guide", true)]
    [InlineData("create_thread", false)]
    [InlineData("spread_items", false)]
    [InlineData("bash", false)]
    [InlineData("write_file", false)]
    [InlineData("delete_file", false)]
    public void ReturnsContent_MatchesOnTokenBoundaries(string toolName, bool expected)
        => Assert.Equal(expected, ToolInvocations.ReturnsContent(toolName));

    private static AgentStreamEvent Event(string type, string json) => new(type, type, null, json);
}
