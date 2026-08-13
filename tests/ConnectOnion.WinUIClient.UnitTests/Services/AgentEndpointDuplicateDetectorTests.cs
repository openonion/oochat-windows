using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class AgentEndpointDuplicateDetectorTests
{
    [Fact]
    public void Find_SameAddressWithDifferentHexCasing_ReturnsAddress()
    {
        var agents = new[]
        {
            Agent(address: "0x" + new string('a', 64)),
        };

        var result = AgentEndpointDuplicateDetector.Find(
            agents,
            "0x" + new string('A', 64),
            directUrl: null);

        Assert.Equal(AgentEndpointDuplicate.Address, result);
    }

    [Fact]
    public void Find_EquivalentDirectUrls_ReturnsDirectUrl()
    {
        var agents = new[]
        {
            Agent(directUrl: "https://agent.example.test:443/api/"),
        };

        var result = AgentEndpointDuplicateDetector.Find(
            agents,
            address: null,
            directUrl: " HTTPS://AGENT.EXAMPLE.TEST/api?token=ignored#fragment ");

        Assert.Equal(AgentEndpointDuplicate.DirectUrl, result);
    }

    [Fact]
    public void Find_SameAddressAndUrlAcrossDifferentAgents_ReturnsBoth()
    {
        var address = "0x" + new string('1', 64);
        var agents = new[]
        {
            Agent(address: address),
            Agent(directUrl: "https://agent.example.test"),
        };

        var result = AgentEndpointDuplicateDetector.Find(
            agents,
            address,
            "https://agent.example.test/");

        Assert.Equal(
            AgentEndpointDuplicate.Address | AgentEndpointDuplicate.DirectUrl,
            result);
    }

    [Fact]
    public void Find_DifferentPathCasing_DoesNotReturnDuplicate()
    {
        var agents = new[]
        {
            Agent(directUrl: "https://agent.example.test/Email"),
        };

        var result = AgentEndpointDuplicateDetector.Find(
            agents,
            address: null,
            directUrl: "https://agent.example.test/email");

        Assert.Equal(AgentEndpointDuplicate.None, result);
    }

    [Fact]
    public void Find_EmptyTargets_DoNotMatchEmptySavedFields()
    {
        var result = AgentEndpointDuplicateDetector.Find(
            new[] { Agent() },
            "  ",
            "  ");

        Assert.Equal(AgentEndpointDuplicate.None, result);
    }

    private static AgentConfig Agent(string address = "", string? directUrl = null)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Agent",
            Address = address,
            DirectUrl = directUrl,
        };
}
