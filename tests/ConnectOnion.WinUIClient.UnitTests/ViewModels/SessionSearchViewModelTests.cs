using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.ViewModels;

namespace ConnectOnion.WinUIClient.UnitTests.ViewModels;

public sealed class SessionSearchViewModelTests
{
    [Fact]
    public void Reset_NoQuery_ShowsMostRecentlyUpdatedFirst()
    {
        var viewModel = new SessionSearchViewModel();

        viewModel.Reset(
        [
            Item("older", "Earlier chat", "remote-admin", "2026-01-01T00:00:00Z"),
            Item("newer", "Latest chat", "my_blog", "2026-02-01T00:00:00Z"),
        ]);

        Assert.Equal(["newer", "older"], viewModel.Results.Select(item => item.SessionId));
        Assert.False(viewModel.IsEmpty);
    }

    [Fact]
    public void SearchText_TitleMatch_IsCaseInsensitive()
    {
        var viewModel = ViewModelWithCatalog();

        viewModel.SearchText = "CLAUDE";

        Assert.Equal("Clarify Claude request", Assert.Single(viewModel.Results).Title);
    }

    [Fact]
    public void SearchText_AgentDisplayName_MatchesFriendlyForm()
    {
        var viewModel = ViewModelWithCatalog();

        viewModel.SearchText = "remote admin";

        Assert.Equal("Clarify Claude request", Assert.Single(viewModel.Results).Title);
    }

    [Fact]
    public void SearchText_MultipleTerms_MustAllMatchTheSameResult()
    {
        var viewModel = ViewModelWithCatalog();

        viewModel.SearchText = "claude remote";

        Assert.Single(viewModel.Results);
        Assert.True(viewModel.HasResults);

        viewModel.SearchText = "claude blog";
        Assert.Empty(viewModel.Results);
        Assert.True(viewModel.IsEmpty);
    }

    [Fact]
    public void Reset_AfterSearch_ClearsQueryAndRestoresNewCatalog()
    {
        var viewModel = ViewModelWithCatalog();
        viewModel.SearchText = "claude";

        viewModel.Reset([Item("fresh", "Fresh chat", "new_agent", "2026-03-01T00:00:00Z")]);

        Assert.Equal("", viewModel.SearchText);
        Assert.Equal("fresh", Assert.Single(viewModel.Results).SessionId);
    }

    [Fact]
    public void ApplyContentMatches_BringsBackConversationsWhoseTranscriptMatches()
    {
        var viewModel = ViewModelWithCatalog();
        viewModel.SearchText = "migration";

        // Neither title mentions "migration", so the title filter alone finds nothing.
        Assert.True(viewModel.IsEmpty);

        viewModel.ApplyContentMatches(
            "migration",
            new Dictionary<string, string> { ["two"] = "we should ship the migration on Friday" });

        var result = Assert.Single(viewModel.Results);
        Assert.Equal("two", result.SessionId);
        // The excerpt explains why a conversation with an unrelated title came back.
        Assert.Contains("migration", result.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyContentMatches_TitleMatch_ShowsNoSnippet()
    {
        var viewModel = ViewModelWithCatalog();
        viewModel.SearchText = "claude";

        viewModel.ApplyContentMatches(
            "claude",
            new Dictionary<string, string> { ["one"] = "asking Claude about the schema" });

        // The title already contains the query, so an excerpt would imply the hit was elsewhere.
        Assert.Equal("", Assert.Single(viewModel.Results).Snippet);
    }

    [Fact]
    public void ApplyContentMatches_ForASupersededQuery_IsIgnored()
    {
        var viewModel = ViewModelWithCatalog();
        viewModel.SearchText = "homepage";

        // A slow query for what the user typed two keystrokes ago must not repopulate the list.
        viewModel.ApplyContentMatches(
            "migration",
            new Dictionary<string, string> { ["one"] = "the migration is done" });

        Assert.Equal("two", Assert.Single(viewModel.Results).SessionId);
    }

    [Fact]
    public void SearchText_Changing_DropsThePreviousQuerysContentMatches()
    {
        var viewModel = ViewModelWithCatalog();
        viewModel.SearchText = "migration";
        viewModel.ApplyContentMatches(
            "migration",
            new Dictionary<string, string> { ["two"] = "ship the migration" });
        Assert.False(viewModel.IsEmpty);

        viewModel.SearchText = "migrations";

        // Stale content matches must not keep a row alive under a query it never matched.
        Assert.True(viewModel.IsEmpty);
    }

    [Theory]
    // Short content is shown whole.
    [InlineData("ship the migration", "migration", "ship the migration")]
    // Newlines and tabs collapse so the excerpt stays one line in a fixed-height row.
    [InlineData("ship the\n\tmigration", "migration", "ship the migration")]
    public void BuildSnippet_ShortContent_IsFlattenedButNotTruncated(
        string content,
        string query,
        string expected)
        => Assert.Equal(expected, SessionSearchItem.BuildSnippet(content, query));

    [Fact]
    public void BuildSnippet_LongContent_CentresOnTheHitRatherThanTakingThePrefix()
    {
        var content = new string('a', 300) + " needle " + new string('b', 300);

        var snippet = SessionSearchItem.BuildSnippet(content, "needle");

        Assert.Contains("needle", snippet, StringComparison.Ordinal);
        Assert.StartsWith("…", snippet, StringComparison.Ordinal);
        Assert.EndsWith("…", snippet, StringComparison.Ordinal);
    }

    private static SessionSearchViewModel ViewModelWithCatalog()
    {
        var viewModel = new SessionSearchViewModel();
        viewModel.Reset(
        [
            Item("one", "Clarify Claude request", "remote-admin", "2026-02-01T00:00:00Z"),
            Item("two", "Update homepage", "my_blog", "2026-01-01T00:00:00Z"),
        ]);
        return viewModel;
    }

    private static SessionSearchItem Item(
        string id,
        string title,
        string agent,
        string updatedAt)
        => new()
        {
            SessionId = id,
            AgentId = agent,
            Title = title,
            AgentName = agent,
            UpdatedAt = updatedAt,
        };
}
