using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class ConversationCacheTests
{
    [Fact]
    public void StoreThenGet_MatchingAgent_ReturnsSameEntry()
    {
        var id = Unique("roundtrip");
        var entry = Entry("agent-a", "hello");

        ConversationCache.Store(id, entry);

        Assert.Same(entry, ConversationCache.Get(id, "agent-a"));
        ConversationCache.Invalidate(id);
    }

    [Fact]
    public void Get_DifferentAgent_ReturnsMissWithoutLeakingConversation()
    {
        var id = Unique("agent-isolation");
        ConversationCache.Store(id, Entry("agent-a", "secret"));

        Assert.Null(ConversationCache.Get(id, "agent-b"));
        Assert.NotNull(ConversationCache.Get(id, "agent-a"));
        ConversationCache.Invalidate(id);
    }

    [Fact]
    public void Store_FifthEntry_EvictsLeastRecentlyUsedEntry()
    {
        var ids = Enumerable.Range(1, 5).Select(i => Unique("evict-" + i)).ToArray();
        foreach (var id in ids.Take(4)) ConversationCache.Store(id, Entry("agent", id));

        ConversationCache.Store(ids[4], Entry("agent", ids[4]));

        Assert.Null(ConversationCache.Get(ids[0], "agent"));
        foreach (var id in ids.Skip(1)) Assert.NotNull(ConversationCache.Get(id, "agent"));
        foreach (var id in ids) ConversationCache.Invalidate(id);
    }

    [Fact]
    public void Get_CacheHit_TouchesEntryForLruEviction()
    {
        var ids = Enumerable.Range(1, 5).Select(i => Unique("touch-" + i)).ToArray();
        foreach (var id in ids.Take(4)) ConversationCache.Store(id, Entry("agent", id));
        Assert.NotNull(ConversationCache.Get(ids[0], "agent"));

        ConversationCache.Store(ids[4], Entry("agent", ids[4]));

        Assert.NotNull(ConversationCache.Get(ids[0], "agent"));
        Assert.Null(ConversationCache.Get(ids[1], "agent"));
        foreach (var id in ids) ConversationCache.Invalidate(id);
    }

    [Fact]
    public void Store_ExistingSession_ReplacesEntryAndTouchesIt()
    {
        var id = Unique("replace");
        ConversationCache.Store(id, Entry("agent", "old"));
        var replacement = Entry("agent", "new");

        ConversationCache.Store(id, replacement);

        Assert.Same(replacement, ConversationCache.Get(id, "agent"));
        Assert.Equal("new", Assert.Single(replacement.Messages).Content);
        ConversationCache.Invalidate(id);
    }

    [Fact]
    public void Invalidate_StoredSession_RemovesEntry()
    {
        var id = Unique("invalidate");
        ConversationCache.Store(id, Entry("agent", "message"));

        ConversationCache.Invalidate(id);

        Assert.Null(ConversationCache.Get(id, "agent"));
    }

    [Fact]
    public void Store_OversizedTranscript_DoesNotPinIt()
    {
        var id = Unique("oversized");
        ConversationCache.Store(id, Entry("agent", new string('x', 9 * 1024 * 1024)));

        Assert.Null(ConversationCache.Get(id, "agent"));
    }

    [Fact]
    public void RepeatedlySwitchingTwoCachedConversations_RemainsBounded()
    {
        var first = Unique("switch-a");
        var second = Unique("switch-b");
        for (var i = 0; i < 10_000; i++)
        {
            var id = (i & 1) == 0 ? first : second;
            ConversationCache.Store(id, Entry("agent", i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.NotNull(ConversationCache.Get(id, "agent"));
        }

        Assert.InRange(ConversationCache.CountForTests, 1, 4);
        ConversationCache.Invalidate(first);
        ConversationCache.Invalidate(second);
    }

    private static ConversationCache.Entry Entry(string agentId, string content) => new(
        new AgentConfig { Id = agentId, Name = "Agent", Address = "0x1" },
        new SessionSummary
        {
            Id = "session",
            AgentId = agentId,
            Title = "Session",
            CreatedAt = "2026-01-01",
            UpdatedAt = "2026-01-01",
        },
        new List<ChatMessage> { new() { Id = 1, Role = ChatRole.User, Content = content } },
        CreatedAt: 1);

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");
}
