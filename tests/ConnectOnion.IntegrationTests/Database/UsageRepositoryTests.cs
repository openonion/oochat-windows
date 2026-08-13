using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.IntegrationTests.Database;

[Collection(DatabaseCollection.Name)]
public sealed class UsageRepositoryTests
{
    private readonly TempDatabaseFixture _fixture;
    private readonly UsageRepository _usage = new();
    private readonly ConversationRepository _conversations = new();

    public UsageRepositoryTests(TempDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task InsertAsync_SameEventTwice_UpdatesWithoutDoubleCounting()
    {
        await _usage.ClearAsync();
        await _usage.InsertAsync(new[] { Record("same-event", input: 100, output: 20) });
        await _usage.InsertAsync(new[] { Record("same-event", input: 150, output: 30) });

        var summary = Assert.Single(await _usage.GetByModelAsync(null));
        Assert.Equal(1, summary.Calls);
        Assert.Equal(150, summary.InputTokens);
        Assert.Equal(30, summary.OutputTokens);
    }

    [Fact]
    public async Task DeleteMessagesAsync_Conversation_DoesNotEraseUsageLedger()
    {
        await _usage.ClearAsync();
        await _fixture.CreateSessionAsync("usage-conversation");
        await _conversations.UpsertMessagesAsync("usage-conversation", new[]
        {
            new ChatMessage { Id = 1, Role = ChatRole.User, Content = "hello" },
        });
        await _usage.InsertAsync(new[] { Record("survives-delete", conversationId: "usage-conversation") });

        await _conversations.DeleteMessagesAsync("usage-conversation");

        var summary = Assert.Single(await _usage.GetByModelAsync(null));
        Assert.Equal(1, summary.Calls);
        Assert.Equal(120, summary.TotalTokens);
    }

    [Fact]
    public async Task GetByModelAsync_GroupsOrdersAndFiltersInSql()
    {
        await _usage.ClearAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _usage.InsertAsync(new[]
        {
            Record("a-1", model: "model-a", agentId: "agent-a", input: 100, output: 50, createdAt: now),
            Record("a-2", model: "model-a", agentId: "agent-a", input: 200, output: 50, createdAt: now),
            Record("b-1", model: "model-b", agentId: "agent-b", input: 10, output: 5, createdAt: now - 100_000),
        });

        var all = await _usage.GetByModelAsync(null);
        Assert.Equal(new[] { "model-a", "model-b" }, all.Select(item => item.Model));
        Assert.Equal(2, all[0].Calls);
        Assert.Equal(400, all[0].TotalTokens);
        Assert.Single(await _usage.GetByModelAsync(null, "agent-b"));
        Assert.Single(await _usage.GetByModelAsync(now - 1_000));
    }

    [Fact]
    public async Task GetFirstRecordedAsync_RecordsExist_ReturnsOldestTimestamp()
    {
        await _usage.ClearAsync();
        var oldest = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds();
        await _usage.InsertAsync(new[]
        {
            Record("newer", createdAt: oldest + 10_000),
            Record("oldest", createdAt: oldest),
        });

        var actual = await _usage.GetFirstRecordedAsync();

        Assert.NotNull(actual);
        Assert.Equal(oldest, actual.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ClearAsync_SinceTimestamp_DeletesOnlyRecentUsage()
    {
        await _usage.ClearAsync();
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _usage.InsertAsync(new[]
        {
            Record("old", createdAt: cutoff - 1),
            Record("recent", createdAt: cutoff),
        });

        await _usage.ClearAsync(cutoff);

        var summary = Assert.Single(await _usage.GetByModelAsync(null));
        Assert.Equal(1, summary.Calls);
    }

    [Fact]
    public async Task GetDailyTotalsAsync_GroupsByLocalCalendarDay_AndSumsBothTokenKinds()
    {
        await _usage.ClearAsync();
        // 09:00 and 21:00 local on the same day. The 21:00 call is the one that matters: with
        // UTC bucketing it lands on tomorrow for anyone east of Greenwich and on the same day
        // for anyone west, so this is what pins the 'localtime' modifier in the query.
        var day = new DateTimeOffset(2026, 3, 14, 9, 0, 0, DateTimeOffset.Now.Offset);
        await _usage.InsertAsync(new[]
        {
            Record("morning", input: 100, output: 20, createdAt: day.ToUnixTimeMilliseconds()),
            Record("evening", input: 300, output: 80, createdAt: day.AddHours(12).ToUnixTimeMilliseconds()),
        });

        var totals = await _usage.GetDailyTotalsAsync(null);

        var only = Assert.Single(totals);
        Assert.Equal(new DateOnly(2026, 3, 14), only.Date);
        Assert.Equal(400, only.InputTokens);
        Assert.Equal(100, only.OutputTokens);
        Assert.Equal(500, only.TotalTokens);
        Assert.Equal(2, only.Calls);
    }

    [Fact]
    public async Task GetDailyTotalsAsync_ReturnsOneRowPerDay_InAscendingOrder()
    {
        await _usage.ClearAsync();
        var first = new DateTimeOffset(2026, 3, 10, 12, 0, 0, DateTimeOffset.Now.Offset);
        await _usage.InsertAsync(new[]
        {
            Record("d3", createdAt: first.AddDays(2).ToUnixTimeMilliseconds()),
            Record("d1", createdAt: first.ToUnixTimeMilliseconds()),
            Record("d2", createdAt: first.AddDays(1).ToUnixTimeMilliseconds()),
        });

        var totals = await _usage.GetDailyTotalsAsync(null);

        Assert.Equal(3, totals.Count);
        Assert.Equal(totals.OrderBy(t => t.Date).Select(t => t.Date), totals.Select(t => t.Date));
    }

    [Fact]
    public async Task GetDailyTotalsAsync_HonoursTheSinceBound()
    {
        await _usage.ClearAsync();
        var now = DateTimeOffset.Now;
        await _usage.InsertAsync(new[]
        {
            Record("old", createdAt: now.AddDays(-40).ToUnixTimeMilliseconds()),
            Record("recent", createdAt: now.AddDays(-2).ToUnixTimeMilliseconds()),
        });

        var totals = await _usage.GetDailyTotalsAsync(now.AddDays(-7).ToUnixTimeMilliseconds());

        Assert.Single(totals);
    }

    [Fact]
    public async Task WindowedQueries_UseCreatedAtRangeIndexes()
    {
        await using var connection = await AppDatabase.OpenAsync();

        await using var daily = connection.CreateCommand();
        daily.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT date(created_at / 1000, 'unixepoch')
            FROM usage_events INDEXED BY ix_usage_created
            WHERE created_at >= $since
            GROUP BY 1;
            """;
        daily.Parameters.AddWithValue("$since", 1L);
        await using var dailyReader = await daily.ExecuteReaderAsync();
        var dailyPlan = new List<string>();
        while (await dailyReader.ReadAsync()) dailyPlan.Add(dailyReader.GetString(3));
        Assert.Contains(dailyPlan, detail =>
            detail.Contains("ix_usage_created (created_at>?)", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public async Task GetDailyTotalsAsync_EmptyLedger_ReturnsEmptyNotNull()
    {
        await _usage.ClearAsync();

        Assert.Empty(await _usage.GetDailyTotalsAsync(null));
    }

    private static UsageRecord Record(
        string id,
        string model = "test-model",
        string? agentId = "agent-1",
        string? conversationId = null,
        long input = 100,
        long output = 20,
        long? createdAt = null) => new(
            Id: id,
            ConversationId: conversationId,
            AgentId: agentId,
            AgentName: "Agent",
            Model: model,
            InputTokens: input,
            OutputTokens: output,
            CachedTokens: 10,
            CacheWriteTokens: 5,
            DurationMs: 42,
            CreatedAt: createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
