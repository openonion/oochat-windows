using System.Text.Json;

namespace ConnectOnion.Protocol.Tests;

public sealed class BufferedEventCompactionTests
{
    [Fact]
    public void LlmCall_DropsRepeatedMessageHistory_ButKeepsProjectionFields()
    {
        var frame = WireMessage.Parse(
            """{"type":"llm_call","id":"evt-1","ts":123,"model":"gpt-test","messages":[{"content":"large-history"}]}""");

        var compact = AgentConnectionService.BufferedEventJson(frame);
        using var parsed = JsonDocument.Parse(compact);

        Assert.Equal("llm_call", parsed.RootElement.GetProperty("type").GetString());
        Assert.Equal("gpt-test", parsed.RootElement.GetProperty("model").GetString());
        Assert.False(parsed.RootElement.TryGetProperty("messages", out _));
        Assert.DoesNotContain("large-history", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void LlmResult_KeepsUsageAndTiming_ButDropsProviderPayload()
    {
        var frame = WireMessage.Parse(
            """{"type":"llm_result","model":"gpt-test","duration_ms":42,"context_percent":25,"usage":{"input_tokens":10,"output_tokens":4,"cached_tokens":2,"cache_write_tokens":1},"response":{"huge":"payload"}}""");

        var compact = AgentConnectionService.BufferedEventJson(frame);
        using var parsed = JsonDocument.Parse(compact);

        Assert.Equal(42, parsed.RootElement.GetProperty("duration_ms").GetInt64());
        Assert.Equal(10, parsed.RootElement.GetProperty("usage").GetProperty("input_tokens").GetInt64());
        Assert.False(parsed.RootElement.TryGetProperty("response", out _));
    }

    [Fact]
    public void ToolEvent_RemainsUnchanged()
    {
        const string json = """{"type":"tool_call","name":"bash","arguments":{"command":"echo ok"}}""";
        Assert.Equal(json, AgentConnectionService.BufferedEventJson(WireMessage.Parse(json)));
    }

    [Fact]
    public void ToolResult_CapsUnboundedPayload_ButKeepsProjectionFields()
    {
        var huge = new string('x', 20_000);
        var frame = WireMessage.Parse($$"""{"type":"tool_result","tool_id":"one","status":"success","result":"{{huge}}"}""");

        var compact = AgentConnectionService.BufferedEventJson(frame);
        using var parsed = JsonDocument.Parse(compact);

        Assert.Equal("one", parsed.RootElement.GetProperty("tool_id").GetString());
        Assert.Equal("success", parsed.RootElement.GetProperty("status").GetString());
        Assert.True(parsed.RootElement.GetProperty("result").GetString()!.Length < huge.Length);
        Assert.Contains("truncated", compact, StringComparison.Ordinal);
    }
}
