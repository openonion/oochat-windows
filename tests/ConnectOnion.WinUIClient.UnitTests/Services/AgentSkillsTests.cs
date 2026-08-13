using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

/// <summary>
/// The slash-palette matcher and the landing-page chip builder. Table-driven because the ranking
/// <i>is</i> the feature: a matcher that puts the wrong skill first looks identical in a screenshot
/// and is obvious in a table.
/// </summary>
public sealed class AgentSkillsTests
{
    private static SkillInfo Skill(string name, string description = "") => new(name, description);

    // ---- MatchRank ----------------------------------------------------------

    [Theory]
    [InlineData("linkedin-engagement", "linked", 0)]     // prefix
    [InlineData("linkedin-engagement", "LINKED", 0)]     // and casing is ignored
    [InlineData("linkedin-engagement", "engagement", 1)] // substring
    [InlineData("linkedin-engagement", "linkedeng", 2)]  // letters in order — the forgiving tier
    [InlineData("linkedin-engagement", "zzz", -1)]
    // Every letter present but out of order is not a match: subsequence, not anagram.
    [InlineData("linkedin-engagement", "gnel", -1)]
    [InlineData("post", "", 0)]
    public void MatchRank_ScoresByHowDirectlyTheNameWasTyped(string name, string query, int expected)
    {
        Assert.Equal(expected, AgentSkills.MatchRank(name, query));
    }

    [Fact]
    public void Match_OrdersPrefixAboveSubstringAboveInOrder()
    {
        // Declared worst-first, so a passing assertion cannot be the input order in disguise.
        var skills = new[]
        {
            Skill("engage-post"),              // "post" appears, but not at the start → substring
            Skill("publish-to-social-thread"), // p·o·s·t in order, never adjacent → in-order
            Skill("post-report"),              // → prefix
        };

        var matched = AgentSkills.Match(skills, "post");

        Assert.Equal(
            ["post-report", "engage-post", "publish-to-social-thread"],
            matched.Select(skill => skill.Name));
    }

    [Fact]
    public void Match_KeepsTheAgentsOwnOrderWithinARank()
    {
        // Stable ordering matters between keystrokes: a list that reshuffles equal-ranked entries
        // moves the highlighted row out from under the user's arrow keys.
        var skills = new[] { Skill("post-a"), Skill("post-b"), Skill("post-c") };

        var matched = AgentSkills.Match(skills, "post");

        Assert.Equal(["post-a", "post-b", "post-c"], matched.Select(skill => skill.Name));
    }

    [Fact]
    public void Match_EmptyQueryListsEverything()
    {
        // What a bare "/" should show.
        var skills = new[] { Skill("a"), Skill("b") };
        Assert.Equal(2, AgentSkills.Match(skills, "").Count);
    }

    [Fact]
    public void Match_NoSkills_ReturnsEmpty()
    {
        Assert.Empty(AgentSkills.Match(null, "post"));
        Assert.Empty(AgentSkills.Match([], "post"));
    }

    // ---- TryGetSlashQuery ---------------------------------------------------

    [Theory]
    [InlineData("/", "")]
    [InlineData("/post", "post")]
    [InlineData("/Post", "Post")]
    public void TryGetSlashQuery_OpenWhileTheCommandIsStillBeingTyped(string text, string expected)
    {
        Assert.True(AgentSkills.TryGetSlashQuery(text, out var query));
        Assert.Equal(expected, query);
    }

    [Theory]
    // The space commits the command: everything after it is arguments, so the palette closes and
    // Enter goes back to sending the message.
    [InlineData("/post ")]
    [InlineData("/post something")]
    [InlineData("/post\nmore")]
    // Not a command at all.
    [InlineData("hello")]
    [InlineData("")]
    [InlineData(null)]
    public void TryGetSlashQuery_ClosedOnceTheCommandIsCommittedOrNeverStarted(string? text)
    {
        Assert.False(AgentSkills.TryGetSlashQuery(text, out _));
    }

    // ---- ChipOffer ----------------------------------------------------------

    [Fact]
    public void ChipOffer_TakesTheFirstSentenceOnly()
    {
        // Later sentences are caveats and mechanics; a chip is an offer.
        Assert.Equal(
            "Publish a post to your feed",
            AgentSkills.ChipOffer("Publish a post to your feed. Requires an authenticated session."));
    }

    [Fact]
    public void ChipOffer_CutsAtAClauseBoundary()
    {
        Assert.Equal(
            "Draft and schedule a weekly update",
            AgentSkills.ChipOffer("Draft and schedule a weekly update, using the saved template"));
    }

    [Fact]
    public void ChipOffer_DoesNotCutAPhraseDownToNothing()
    {
        // The four-word floor: an early comma must not reduce the offer to "Publish a".
        Assert.Equal("Publish a post now", AgentSkills.ChipOffer("Publish a post now, quickly"));
    }

    [Theory]
    // Ends on a preposition — reads as a truncation bug rather than an offer.
    [InlineData("Send a message to")]
    // Too long to read at a glance.
    [InlineData("Coordinate an end-to-end multi-channel publishing workflow across every configured account")]
    // Too short to be an offer.
    [InlineData("Search")]
    [InlineData("")]
    [InlineData(null)]
    public void ChipOffer_ReturnsNullRatherThanABadChip(string? description)
    {
        // Null is a real answer: a bad chip is worse than no chip, because it is the first thing
        // the user reads about the agent.
        Assert.Null(AgentSkills.ChipOffer(description));
    }

    [Fact]
    public void ChipOffer_FixesBrandCasing()
    {
        Assert.Equal("Reply to LinkedIn comments", AgentSkills.ChipOffer("Reply to linkedin comments"));
    }

    // ---- BestOffers ---------------------------------------------------------

    [Fact]
    public void BestOffers_PutsOutcomesBeforeMechanismsAndCapsTheRow()
    {
        var skills = new[]
        {
            Skill("a", "Analyze the traffic report for anomalies"),
            Skill("b", "Handle the request pipeline end to end"),
            Skill("c", "Publish a post to your feed"),
            Skill("d", "Manage every configured account"),
        };

        var offers = AgentSkills.BestOffers(skills);

        Assert.Equal(3, offers.Count);
        // "Publish" and "Analyze" lead with an outcome; between them the shorter one wins.
        Assert.Equal("Publish a post to your feed", offers[0]);
        Assert.Equal("Analyze the traffic report for anomalies", offers[1]);
    }

    [Fact]
    public void BestOffers_KeepsInternalSkillsOffTheChipRow()
    {
        var skills = new[]
        {
            Skill("debug-capture", "Capture the debug state for a failing run"),
            Skill("internal-sync", "Called by other skills to refresh the token cache"),
            Skill("post", "Publish a post to your feed"),
        };

        var offers = AgentSkills.BestOffers(skills);

        // They stay perfectly usable from the slash palette — this is a first-impression surface,
        // not a permission boundary.
        Assert.Equal(["Publish a post to your feed"], offers);
        Assert.Equal(3, AgentSkills.Match(skills, "").Count);
    }

    [Fact]
    public void BestOffers_AgentWithNoUsableDescriptions_YieldsNothingToFallBackFrom()
    {
        var skills = new[] { Skill("run"), Skill("x", "Do"), Skill("y", "Send to") };

        Assert.Empty(AgentSkills.BestOffers(skills));
    }

    [Fact]
    public void BestOffers_DeduplicatesIdenticalPhrasings()
    {
        var skills = new[]
        {
            Skill("post-now", "Publish a post to your feed"),
            Skill("post-later", "Publish a post to your feed, on a schedule"),
        };

        Assert.Single(AgentSkills.BestOffers(skills));
    }

    [Fact]
    public void BestOffers_NoSkills_ReturnsEmpty()
    {
        Assert.Empty(AgentSkills.BestOffers(null));
        Assert.Empty(AgentSkills.BestOffers([]));
    }

    [Fact]
    public void CompleteOffers_FillsUnusedSlotsAfterAgentOffers()
    {
        var completed = AgentSkills.CompleteOffers(
            ["Safely find Gmail friend-link requests"],
            ["Summarize your capabilities", "Help me get started", "Suggest three useful tasks"]);

        Assert.Equal(
            [
                "Safely find Gmail friend-link requests",
                "Summarize your capabilities",
                "Help me get started",
            ],
            completed);
    }

    [Fact]
    public void CompleteOffers_DeduplicatesAndStillFillsThreeSlots()
    {
        var completed = AgentSkills.CompleteOffers(
            ["Publish a post", "publish a post"],
            ["Publish a post", "Help me get started", "Suggest three useful tasks"]);

        Assert.Equal(
            ["Publish a post", "Help me get started", "Suggest three useful tasks"],
            completed);
    }

    [Fact]
    public void CompleteOffers_UsesFallbacksWhenAgentHasNoOffers()
    {
        var completed = AgentSkills.CompleteOffers(
            [],
            ["One", "Two", "Three", "Four"]);

        Assert.Equal(["One", "Two", "Three"], completed);
    }
}

/// <summary>
/// Reading skills back out of a cached <c>/info</c> blob — the path every UI surface takes, since
/// the agent record already holds the JSON and must not hit the network to draw a palette.
/// </summary>
public sealed class ParseSkillsFromInfoJsonTests
{
    [Fact]
    public void ParseSkillsFromInfoJson_ReadsNameDescriptionAndLocation()
    {
        var skills = EndpointResolver.ParseSkillsFromInfoJson(
            """{"skills":[{"name":"post","description":"Publish a post","location":".co/skills/post"}]}""");

        var skill = Assert.Single(skills);
        Assert.Equal("post", skill.Name);
        Assert.Equal("Publish a post", skill.Description);
        Assert.Equal(".co/skills/post", skill.Location);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"skills":"nope"}""")]
    // A skill with no name cannot be typed as a command, so it is dropped rather than shown blank.
    [InlineData("""{"skills":[{"description":"nameless"}]}""")]
    public void ParseSkillsFromInfoJson_DegradesToNoSkillsRatherThanFailing(string? json)
    {
        Assert.Empty(EndpointResolver.ParseSkillsFromInfoJson(json));
    }
}
