using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class SessionSelectionTests
{
    [Fact]
    public void FindExisting_ActiveSessionBelongsToAgent_ReturnsActiveSession()
    {
        var active = Session("active", "agent", "2026-07-01T00:00:00Z");
        var newer = Session("newer", "agent", "2026-07-02T00:00:00Z");

        Assert.Same(active, SessionSelection.FindExisting([active, newer], active.Id, "agent"));
    }

    [Fact]
    public void FindExisting_ActiveSessionBelongsToAnotherAgent_ReturnsMostRecentForAgent()
    {
        var older = Session("older", "agent", "2026-07-01T00:00:00Z");
        var newer = Session("newer", "agent", "2026-07-02T00:00:00Z");
        var other = Session("other", "other-agent", "2026-07-03T00:00:00Z");

        Assert.Same(newer, SessionSelection.FindExisting([older, newer, other], other.Id, "agent"));
    }

    [Fact]
    public void FindExisting_NoSessionForAgent_ReturnsNullInsteadOfCreatingSession()
    {
        var sessions = new[] { Session("other", "other-agent", "2026-07-03T00:00:00Z") };

        Assert.Null(SessionSelection.FindExisting(sessions, sessions[0].Id, "agent"));
        Assert.Single(sessions);
    }

    private static SessionSummary Session(string id, string agentId, string updatedAt) => new()
    {
        Id = id,
        AgentId = agentId,
        Title = id,
        CreatedAt = updatedAt,
        UpdatedAt = updatedAt,
    };
}
