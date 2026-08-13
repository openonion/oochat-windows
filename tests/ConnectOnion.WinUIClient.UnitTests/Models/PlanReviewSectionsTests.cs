using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class PlanReviewSectionsTests
{
    [Fact]
    public void Parse_SplitsMarkdownHeadingsAndPreservesBodies()
    {
        var sections = PlanReviewSections.Parse("""
            Intro before headings.
            ## Prepare
            - Back up data
            ### Deploy
            Run the release.
            """);

        Assert.Equal(3, sections.Count);
        Assert.Equal("Overview", sections[0].Heading);
        Assert.Equal("Prepare", sections[1].Heading);
        Assert.Contains("- Back up data", sections[1].Markdown);
        Assert.Equal("Deploy", sections[2].Heading);
    }

    [Fact]
    public void Parse_WithoutHeading_ReturnsOnePlanSection()
    {
        var section = Assert.Single(PlanReviewSections.Parse("1. Build\n2. Test"));

        Assert.Equal("Overview", section.Heading);
        Assert.Contains("Build", section.Markdown);
    }
}
