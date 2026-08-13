using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.IntegrationTests.Runtime;

public sealed class AgentConnectionRegistryTests : IAsyncLifetime
{
    private readonly HttpClient _http = new();
    private readonly List<AgentConnectionRegistry> _registries = [];

    [Fact]
    public async Task GetOrCreate_ConcurrentCallsForSameConversation_CreateOneConnection()
    {
        var created = 0;
        var registry = Registry(_ =>
        {
            Interlocked.Increment(ref created);
            return Connection();
        });

        var connections = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => Task.Run(() => registry.GetOrCreate("conversation", Agent()))));

        Assert.Equal(1, created);
        Assert.All(connections, connection => Assert.Same(connections[0], connection));
    }

    [Fact]
    public void GetOrCreate_DifferentConversations_HaveIndependentConnections()
    {
        var registry = Registry(_ => Connection());

        var first = registry.GetOrCreate("conversation-a", Agent());
        var second = registry.GetOrCreate("conversation-b", Agent());

        Assert.NotSame(first, second);
        Assert.Same(first, registry.Get("conversation-a"));
        Assert.Same(second, registry.Get("conversation-b"));
    }

    [Fact]
    public async Task RemoveAsync_RemovesOnlyRequestedConversation_AndAllowsRecreation()
    {
        var registry = Registry(_ => Connection());
        var original = registry.GetOrCreate("remove", Agent());
        var retained = registry.GetOrCreate("retain", Agent());

        await registry.RemoveAsync("remove");

        Assert.Null(registry.Get("remove"));
        Assert.Same(retained, registry.Get("retain"));
        Assert.NotSame(original, registry.GetOrCreate("remove", Agent()));
    }

    [Fact]
    public async Task TrimIdleAsync_MoreThanFiveIdleConnections_EvictsLeastRecentlyUsed()
    {
        long tick = 0;
        var registry = Registry(_ => Connection(), () => Interlocked.Increment(ref tick));
        var ids = Enumerable.Range(1, 6).Select(i => $"conversation-{i}").ToArray();
        foreach (var id in ids)
            registry.GetOrCreate(id, Agent());

        await registry.TrimIdleAsync(_ => false);

        Assert.Null(registry.Get(ids[0]));
        foreach (var id in ids.Skip(1))
            Assert.NotNull(registry.Get(id));
    }

    [Fact]
    public async Task TrimIdleAsync_BusyConversation_IsNeverEvicted()
    {
        long tick = 0;
        var registry = Registry(_ => Connection(), () => Interlocked.Increment(ref tick));
        var ids = Enumerable.Range(1, 7).Select(i => $"conversation-{i}").ToArray();
        foreach (var id in ids)
            registry.GetOrCreate(id, Agent());

        await registry.TrimIdleAsync(id => id == ids[0]);

        Assert.NotNull(registry.Get(ids[0]));
        Assert.Null(registry.Get(ids[1]));
        Assert.NotNull(registry.Get(ids[2]));
    }

    [Fact]
    public async Task DisposeAllAsync_ClearsEveryConversation_AndIsIdempotent()
    {
        var registry = Registry(_ => Connection());
        registry.GetOrCreate("conversation-a", Agent());
        registry.GetOrCreate("conversation-b", Agent());

        await registry.DisposeAllAsync();
        await registry.DisposeAllAsync();

        Assert.Null(registry.Get("conversation-a"));
        Assert.Null(registry.Get("conversation-b"));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var registry in _registries)
            await registry.DisposeAllAsync();
        _http.Dispose();
    }

    private AgentConnectionRegistry Registry(
        Func<AgentConfig, AgentConnectionService> factory,
        Func<long>? clock = null)
    {
        var registry = new AgentConnectionRegistry(_http, factory, clock);
        _registries.Add(registry);
        return registry;
    }

    private AgentConnectionService Connection() => new(
        "0x" + new string('1', 64),
        "http://127.0.0.1:1",
        AgentIdentity.Generate(),
        _http);

    private static AgentConfig Agent() => new()
    {
        Id = "agent",
        Name = "Agent",
        Address = "0x" + new string('1', 64),
        DirectUrl = "http://127.0.0.1:1",
    };
}
