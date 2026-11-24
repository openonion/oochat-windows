namespace ConnectOnion.Protocol;

/// <summary>
/// An immutable dashboard page sent by the authenticated Host. The HTML is untrusted
/// agent-authored content and must be rendered inside the client's sandbox.
/// </summary>
public sealed record DashboardSnapshot(string Html, string? SessionId);
