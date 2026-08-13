using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class AgentConfigTests
{
    [Fact]
    public void IsRelayOnly_RequiresAddressWithoutDirectUrl()
    {
        var relay = new AgentConfig { Address = "0xabc", DirectUrl = null };
        var direct = new AgentConfig
        {
            Address = "0xabc",
            DirectUrl = "ws://localhost:8000/ws",
        };
        var incomplete = new AgentConfig { Address = "", DirectUrl = null };

        Assert.True(relay.IsRelayOnly);
        Assert.False(direct.IsRelayOnly);
        Assert.False(incomplete.IsRelayOnly);
    }

    [Fact]
    public void IsRelayOnly_NotifiesWhenTransportChanges()
    {
        var agent = new AgentConfig { Address = "0xabc" };
        var changed = new List<string?>();
        agent.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        agent.DirectUrl = "ws://localhost:8000/ws";

        Assert.Contains(nameof(AgentConfig.IsRelayOnly), changed);
    }
}
