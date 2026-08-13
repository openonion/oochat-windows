using ConnectOnion.Protocol;
using ConnectOnion.Protocol.Runtime;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.UnitTests.Runtime;

public sealed class UsageProjectorTests
{
    [Fact]
    public void Extract_TwoModels_ReturnsOneAttributedRowPerLlmResult()
    {
        var snapshot = Snapshot(
            Event("llm_result", "event-1", "{\"model\":\"model-a\",\"usage\":{\"input_tokens\":100,\"output_tokens\":20,\"cached_tokens\":10},\"duration_ms\":50,\"ts\":1700000000}"),
            Event("thinking", "ignored", "{}"),
            Event("llm_result", "event-2", "{\"model\":\"model-b\",\"usage\":{\"input_tokens\":200,\"output_tokens\":40,\"cache_write_tokens\":5},\"duration_ms\":75,\"ts\":1700000000000}"));

        var rows = UsageProjector.Extract(snapshot, "Agent Name");

        Assert.Collection(rows,
            first =>
            {
                Assert.Equal("event-1", first.Id);
                Assert.Equal("model-a", first.Model);
                Assert.Equal(100, first.InputTokens);
                Assert.Equal(20, first.OutputTokens);
                Assert.Equal(10, first.CachedTokens);
                Assert.Equal(1_700_000_000_000, first.CreatedAt);
            },
            second =>
            {
                Assert.Equal("event-2", second.Id);
                Assert.Equal("model-b", second.Model);
                Assert.Equal(5, second.CacheWriteTokens);
                Assert.Equal(1_700_000_000_000, second.CreatedAt);
            });
        Assert.All(rows, row =>
        {
            Assert.Equal("conversation", row.ConversationId);
            Assert.Equal("agent", row.AgentId);
            Assert.Equal("Agent Name", row.AgentName);
        });
    }

    [Fact]
    public void Extract_MalformedModelLessAndZeroUsageEvents_SkipsWithoutThrowing()
    {
        var snapshot = Snapshot(
            Event("llm_result", "bad-json", "{"),
            Event("llm_result", "no-model", "{\"usage\":{\"input_tokens\":10}}"),
            Event("llm_result", "blank-model", "{\"model\":\"  \",\"usage\":{\"input_tokens\":10}}"),
            Event("llm_result", "zero", "{\"model\":\"model\",\"usage\":{}}"));

        Assert.Empty(UsageProjector.Extract(snapshot, null));
    }

    [Fact]
    public void Extract_FailedRun_StillReturnsUsageRows()
    {
        var snapshot = Snapshot(
            Event("llm_result", "spent", "{\"model\":\"model\",\"usage\":{\"input_tokens\":12}}"))
            with
        { Status = ConversationRunStatus.Failed, ErrorMessage = "failed" };

        Assert.Equal(12, Assert.Single(UsageProjector.Extract(snapshot, "Agent")).InputTokens);
    }

    private static ConversationRunSnapshot Snapshot(params AgentStreamEvent[] events) => new(
        RunId: "run",
        ConversationId: "conversation",
        AgentId: "agent",
        UserMessageId: "user-message",
        AssistantMessageId: "assistant-message",
        Status: ConversationRunStatus.Completed,
        PartialContent: "",
        Sequence: events.Length,
        ErrorCode: null,
        ErrorMessage: null,
        Events: events);

    private static AgentStreamEvent Event(string type, string? id, string json) => new(type, type, id, json);
}
