namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// One LLM call's token usage — the write side of the usage ledger, one row per
/// <c>llm_result</c> event. Per call rather than per turn on purpose: a single turn can hit more
/// than one model (a fallback, a sub-agent), and collapsing that to one row per turn would destroy
/// exactly the dimension the Usage panel exists to show.
/// </summary>
/// <param name="AgentName">Snapshotted at write time. The ledger outlives the agent, so without a
/// copy of the name a deleted agent's rows could only be labelled with a GUID.</param>
public sealed record UsageRecord(
    string Id,
    string? ConversationId,
    string? AgentId,
    string? AgentName,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CachedTokens,
    long CacheWriteTokens,
    double? DurationMs,
    long CreatedAt)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>One row of the Usage panel: everything spent on a single model in the selected window.</summary>
public sealed record ModelUsageSummary(
    string Model,
    long Calls,
    long InputTokens,
    long OutputTokens,
    long CachedTokens,
    long CacheWriteTokens,
    double TotalDurationMs)
{
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>Share of cached input, which is usually the answer to "why is this model cheap?".
    /// Zero (not NaN) when the model has taken no input tokens at all.</summary>
    public double CacheHitRatio => InputTokens <= 0 ? 0 : (double)CachedTokens / InputTokens;
}

/// <summary>The time window the Usage panel is showing.</summary>
public enum UsageRange
{
    Today,
    Last7Days,
    Last30Days,
    AllTime,
}

public static class UsageRangeExtensions
{
    /// <summary>Inclusive lower bound in Unix milliseconds, or null for <see cref="UsageRange.AllTime"/>.</summary>
    public static long? SinceUnixMs(this UsageRange range)
    {
        var now = DateTimeOffset.Now;
        return range switch
        {
            UsageRange.Today => new DateTimeOffset(now.Date, now.Offset).ToUnixTimeMilliseconds(),
            UsageRange.Last7Days => now.AddDays(-7).ToUnixTimeMilliseconds(),
            UsageRange.Last30Days => now.AddDays(-30).ToUnixTimeMilliseconds(),
            _ => null,
        };
    }

    public static string Label(this UsageRange range) => range switch
    {
        UsageRange.Today => Common.CoreStrings.Get("UsageRangeToday", "Today"),
        UsageRange.Last7Days => Common.CoreStrings.Get("UsageRange7Days", "Last 7 days"),
        UsageRange.Last30Days => Common.CoreStrings.Get("UsageRange30Days", "Last 30 days"),
        _ => Common.CoreStrings.Get("UsageRangeAllTime", "All time"),
    };
}
