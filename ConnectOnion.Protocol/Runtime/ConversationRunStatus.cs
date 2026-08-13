namespace ConnectOnion.Protocol.Runtime;

/// <summary>
/// Lifecycle of a single agent turn (one prompt → one final reply), owned by the
/// app-level <see cref="ConversationRunRegistry"/> rather than any page/view model.
/// </summary>
public enum ConversationRunStatus
{
    /// <summary>Created, not yet handed to the executor.</summary>
    Queued,

    /// <summary>The executor is opening/authenticating the transport.</summary>
    Connecting,

    /// <summary>The agent is producing the reply (streaming intermediate events).</summary>
    Running,

    /// <summary>Final reply received and durably persisted.</summary>
    Completed,

    /// <summary>The turn ended with an error; partial content and the error are retained.</summary>
    Failed,

    /// <summary>The turn was cancelled (user stop or app shutdown).</summary>
    Cancelled,
}

public static class ConversationRunStatusExtensions
{
    /// <summary>Terminal states are set exactly once and never transition again.</summary>
    public static bool IsTerminal(this ConversationRunStatus status) =>
        status is ConversationRunStatus.Completed
            or ConversationRunStatus.Failed
            or ConversationRunStatus.Cancelled;
}
