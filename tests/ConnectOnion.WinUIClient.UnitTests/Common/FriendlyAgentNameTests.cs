using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Common;

public sealed class FriendlyAgentNameTests
{
    [Theory]
    [InlineData("remote-admin-agent", "Remote Admin Agent")]
    [InlineData("multimodal_agent", "Multimodal Agent")]
    [InlineData("email_assistant", "Email Assistant")]
    [InlineData("browser_agent", "Browser Agent")]
    [InlineData("agent", "Agent")]
    public void From_SnakeOrKebab_TitleCasesWords(string raw, string expected)
    {
        Assert.Equal(expected, FriendlyAgentName.From(raw));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void From_Blank_ReturnsEmpty(string raw, string expected)
    {
        Assert.Equal(expected, FriendlyAgentName.From(raw));
    }

    [Fact]
    public void From_Null_ReturnsEmpty()
    {
        Assert.Equal("", FriendlyAgentName.From(null));
    }

    [Fact]
    public void From_CollapsesRepeatedAndLeadingSeparators()
    {
        Assert.Equal("Remote Admin", FriendlyAgentName.From("--remote__admin--"));
    }

    [Fact]
    public void From_PreservesInteriorCasingOfAWord()
    {
        // Only the first letter of each word is touched, so an intentional acronym survives.
        Assert.Equal("My API Agent", FriendlyAgentName.From("my-API-agent"));
    }

    [Fact]
    public void From_AlreadyFriendlyProse_ReturnedUnchanged()
    {
        Assert.Equal("Customer Support", FriendlyAgentName.From("Customer Support"));
    }

    [Fact]
    public void AgentConfig_DisplayName_UsesSharedProjectionWithoutChangingStoredName()
    {
        var agent = new AgentConfig { Name = "remote-admin-agent" };

        Assert.Equal("remote-admin-agent", agent.Name);
        Assert.Equal("Remote Admin Agent", agent.DisplayName);
    }

    [Fact]
    public void AgentConfig_TryRename_TrimsAndAppliesLocalDisplayName()
    {
        var agent = new AgentConfig { Name = "remote-admin-agent" };

        Assert.True(agent.TryRename("  My Admin  "));
        Assert.Equal("My Admin", agent.Name);
        Assert.Equal("My Admin", agent.DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AgentConfig_TryRename_RejectsBlankNames(string? name)
    {
        var agent = new AgentConfig { Name = "Original" };

        Assert.False(agent.TryRename(name));
        Assert.Equal("Original", agent.Name);
    }

    [Fact]
    public void AgentConfig_TryRename_RejectsNamesOverTheLimit()
    {
        var agent = new AgentConfig { Name = "Original" };

        Assert.False(agent.TryRename(new string('x', AgentConfig.MaxNameLength + 1)));
        Assert.Equal("Original", agent.Name);
    }

    [Fact]
    public void AgentConfig_TryRename_AcceptsTheExactLimit()
    {
        var agent = new AgentConfig { Name = "Original" };
        var name = new string('x', AgentConfig.MaxNameLength);

        Assert.True(agent.TryRename(name));
        Assert.Equal(name, agent.Name);
    }

    [Fact]
    public void AgentConfig_TryRename_RejectsAnUnchangedTrimmedName()
    {
        var agent = new AgentConfig { Name = "Original" };

        Assert.False(agent.TryRename("  Original  "));
        Assert.Equal("Original", agent.Name);
    }
}
