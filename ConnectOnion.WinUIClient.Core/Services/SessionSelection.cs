using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services;

/// <summary>
/// Picks which existing conversation to open when the user selects an agent rather than a
/// specific conversation (clicking the agent row in the sidebar, activating a notification).
/// Pure and list-driven so the choice is testable without a database.
/// </summary>
public static class SessionSelection
{
    /// <summary>
    /// Returns the conversation to reopen for <paramref name="agentId"/>, or null if the
    /// agent has none yet (the caller then creates one).
    /// </summary>
    /// <param name="activeSessionId">The conversation currently on screen, if any. It wins
    /// whenever it belongs to this agent, so re-selecting the agent you are already talking
    /// to is a no-op instead of a jump to some other conversation.</param>
    public static SessionSummary? FindExisting(
        IReadOnlyList<SessionSummary> sessions,
        string? activeSessionId,
        string agentId)
    {
        // The agent check matters: the active conversation usually belongs to a *different*
        // agent (that's why the user is switching), and it must not be returned then.
        var active = sessions.FirstOrDefault(
            session => session.Id == activeSessionId && session.AgentId == agentId);
        if (active is not null)
        {
            return active;
        }

        // Otherwise the most recently touched one. UpdatedAt is an ISO-8601 string, whose
        // lexicographic order is its chronological order — so an ordinal string sort is
        // correct here and avoids parsing every row just to rank them. Keep the timestamps
        // in that format (fixed-width, UTC) or this comparison quietly stops being right.
        return sessions
            .Where(session => session.AgentId == agentId)
            .OrderByDescending(session => session.UpdatedAt, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
