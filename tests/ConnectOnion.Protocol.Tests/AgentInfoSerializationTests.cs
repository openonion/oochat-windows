namespace ConnectOnion.Protocol.Tests;

/// <summary>
/// Covers <see cref="EndpointResolver.SerializeAgentInfo"/>, the relay path's <c>/info</c> writer.
///
/// <para>The point of every test here is the <b>round trip</b>: whatever this writes must come
/// back out through the parsers that read a stored <c>info_json</c> blob. Before it existed the
/// caller handed an anonymous type to the reflection serializer, which named the nested members
/// after their C# properties (<c>Name</c>, <c>MaxFileSizeMb</c>) while the parsers looked for
/// <c>name</c> and <c>max_file_size_mb</c> — so a relay-composed blob read back with no skills
/// and no declared capabilities, and nothing failed loudly enough to notice.</para>
/// </summary>
public class AgentInfoSerializationTests
{
    private static AgentInfo Full() => new(
        Address: "0xabc",
        Online: true,
        Name: "researcher",
        Tools: ["bash", "search_web"],
        Skills:
        [
            new SkillInfo("linkedin-engagement", "Draft replies to comments", "skills/li.md"),
            new SkillInfo("summarize", "Summarize a page"),
        ],
        Trust: "verified",
        Version: "1.4.0",
        Model: "claude-opus-5",
        AcceptedInputs: new AgentAcceptedInputs(
            Text: true, Images: true, Files: new AgentFileInputs(10, 5)));

    [Fact]
    public void Skills_SurviveTheRoundTrip()
    {
        var skills = EndpointResolver.ParseSkillsFromInfoJson(
            EndpointResolver.SerializeAgentInfo(Full()));

        Assert.Equal(2, skills.Count);
        Assert.Equal("linkedin-engagement", skills[0].Name);
        Assert.Equal("Draft replies to comments", skills[0].Description);
        Assert.Equal("skills/li.md", skills[0].Location);
        Assert.Equal("summarize", skills[1].Name);
        Assert.Null(skills[1].Location);
    }

    [Fact]
    public void AcceptedInputs_SurviveTheRoundTrip()
    {
        var inputs = EndpointResolver.ParseAcceptedInputsFromInfoJson(
            EndpointResolver.SerializeAgentInfo(Full()));

        Assert.NotNull(inputs);
        Assert.True(inputs!.Text);
        Assert.True(inputs.Images);
        Assert.NotNull(inputs.Files);
        Assert.Equal(10, inputs.Files!.MaxFileSizeMb);
        Assert.Equal(5, inputs.Files.MaxFilesPerRequest);
    }

    [Fact]
    public void ScalarFields_UseTheDocumentedKeys()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            EndpointResolver.SerializeAgentInfo(Full()));
        var root = doc.RootElement;

        Assert.Equal("0xabc", root.GetProperty("address").GetString());
        Assert.True(root.GetProperty("online").GetBoolean());
        Assert.Equal("researcher", root.GetProperty("name").GetString());
        Assert.Equal("verified", root.GetProperty("trust").GetString());
        Assert.Equal("1.4.0", root.GetProperty("version").GetString());
        Assert.Equal("claude-opus-5", root.GetProperty("model").GetString());
        Assert.Equal(["bash", "search_web"],
            root.GetProperty("tools").EnumerateArray().Select(t => t.GetString()!).ToArray());
    }

    /// <summary>
    /// Unstated is not the same as false anywhere in the capability model, so an agent that said
    /// nothing must produce a document that says nothing — not one full of nulls that the parser
    /// then has to re-interpret.
    /// </summary>
    [Fact]
    public void UnstatedFields_AreOmittedRatherThanWrittenNull()
    {
        var json = EndpointResolver.SerializeAgentInfo(new AgentInfo("0xabc", Online: false));

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("name", out _));
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("skills", out _));
        Assert.False(root.TryGetProperty("accepted_inputs", out _));
        Assert.False(root.GetProperty("online").GetBoolean());

        Assert.Empty(EndpointResolver.ParseSkillsFromInfoJson(json));
        Assert.Null(EndpointResolver.ParseAcceptedInputsFromInfoJson(json));
    }

    /// <summary>
    /// A partially declared capability set must not be promoted to a complete one: an agent that
    /// only mentioned images leaves text unstated, and the composed blob has to preserve that.
    /// </summary>
    [Fact]
    public void PartialAcceptedInputs_StayPartial()
    {
        var json = EndpointResolver.SerializeAgentInfo(new AgentInfo(
            "0xabc", Online: true, AcceptedInputs: new AgentAcceptedInputs(Images: false)));

        var inputs = EndpointResolver.ParseAcceptedInputsFromInfoJson(json);

        Assert.NotNull(inputs);
        Assert.Null(inputs!.Text);
        Assert.False(inputs.Images);
        Assert.Null(inputs.Files);
    }

    [Fact]
    public void NonAsciiMetadata_RoundTrips()
    {
        var json = EndpointResolver.SerializeAgentInfo(new AgentInfo(
            "0xabc", Online: true, Name: "研究员",
            Skills: [new SkillInfo("总结", "把网页总结成要点")]));

        var skills = EndpointResolver.ParseSkillsFromInfoJson(json);
        Assert.Equal("总结", Assert.Single(skills).Name);
        Assert.Equal("把网页总结成要点", skills[0].Description);
    }
}
