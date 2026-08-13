using System.Collections.Generic;

namespace ConnectOnion.Protocol.Runtime;

/// <summary>
/// Immutable, thread-safe view of a <see cref="ConversationRun"/> published to
/// subscribers. Never hands out the live mutable run, so a UI subscriber can hold
/// a snapshot without racing the background turn that produced it.
/// </summary>
/// <param name="Sequence">Monotonic per-run counter. A resubscribing view model that
/// already applied up to sequence N can ignore an older snapshot and only apply the
/// delta, and multiple windows converge on the same latest sequence.</param>
/// <param name="Events">The ordered raw stream events buffered so far. A page created
/// mid-turn replays these to reconstruct the exact same bubbles it would have shown
/// had it been open the whole time.</param>
public sealed record ConversationRunSnapshot(
    string RunId,
    string ConversationId,
    string AgentId,
    string UserMessageId,
    string AssistantMessageId,
    ConversationRunStatus Status,
    string PartialContent,
    long Sequence,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<AgentStreamEvent> Events)
{
    /// <summary>True once the run has reached a terminal state (Completed/Failed/Cancelled).</summary>
    public bool IsTerminal => Status.IsTerminal();
}
