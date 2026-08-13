using System.Text.Json;

namespace ConnectOnion.Protocol.Tests;

public sealed class AgentInteractiveParsersTests
{
    [Fact]
    public void ParseAskUser_AcceptsRemoteAdminQuestionAndFormSchema()
    {
        using var document = JsonDocument.Parse("""
            {"type":"ask_user","question":"Choose targets","options":["A","B"],
             "multi_select":true,"fields":[{"name":"note","label":"Note","type":"text"}]}
            """);

        var request = AgentInteractiveParsers.ParseAskUser(WireMessage.Wrap(document.RootElement));

        Assert.Equal("Choose targets", request.Text);
        Assert.True(request.MultiSelect);
        Assert.Equal(["A", "B"], request.Options);
        Assert.Equal("note", Assert.Single(request.Fields).Name);
    }
}
