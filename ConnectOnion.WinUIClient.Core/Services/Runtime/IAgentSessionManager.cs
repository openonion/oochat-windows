using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ConnectOnion.Protocol.Runtime;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>Result of submitting a message: the stable ids the caller uses to correlate
/// its optimistic UI with the app-level run.</summary>
/// <param name="PersistedMessageId">Row id of the user bubble written before the run started.
/// Carried back so the caller can un-send: the row exists from the moment of submit (a page
/// opening mid-turn has to see it), so backing out of a send has to delete it by id.</param>
public sealed record SendMessageResult(
    string RunId, string UserMessageId, string AssistantMessageId, long PersistedMessageId);

/// <summary>Result of submitting a graceful stop for the active turn.</summary>
public enum StopOutcome
{
    /// <summary>Nothing was running.</summary>
    NotRunning,

    /// <summary>INTERRUPT was sent successfully on the active connection.</summary>
    StopRequested,
}

/// <summary>
/// App-level owner of agent connections and in-flight turns. Pages/view models depend on
/// this singleton instead of holding a connection or a background task themselves, so a
/// turn keeps running (and persists) after the page is gone, and re-opening a conversation
/// resumes its live stream. See <see cref="ConversationRunRegistry"/> for the guarantees.
/// </summary>
public interface IAgentSessionManager
{
    /// <summary>Persists the user message, then starts an app-level run for the reply. Throws
    /// if the conversation already has an active run (callers gate on <see cref="GetActiveRun"/>).
    /// <paramref name="mode"/> is the approval mode the turn runs under; pass the conversation's
    /// current one on every send (the host will not remember ours between turns).</summary>
    Task<SendMessageResult> SendMessageAsync(
        AgentConfig agent,
        string conversationId,
        string prompt,
        IReadOnlyList<PendingAttachment>? attachments = null,
        string? mode = null);

    /// <summary>Persists and forwards a user interjection to the turn already running in this
    /// conversation. No second local run or execution row is created; the existing run continues
    /// to own stream events and the eventual OUTPUT.</summary>
    Task SendRuntimeInputAsync(
        AgentConfig agent,
        string conversationId,
        string prompt,
        IReadOnlyList<PendingAttachment>? attachments = null);

    ConversationRunSnapshot? GetActiveRun(string conversationId);

    /// <summary>Whether the run's projected conversation history has already been written
    /// to the local database. This is distinct from <see cref="ConversationRunSnapshot.IsTerminal"/>:
    /// persistence completes just before the registry publishes the terminal snapshot.</summary>
    bool IsRunHistoryPersisted(string runId);

    IReadOnlyList<ConversationRunSnapshot> GetActiveRuns();

    /// <summary>Subscribe to a conversation's run updates; the handler fires immediately with
    /// the current snapshot (if any). Disposing detaches without cancelling the run.</summary>
    IDisposable Subscribe(string conversationId, Action<ConversationRunSnapshot> handler);

    Task CancelRunAsync(string conversationId, string runId);

    /// <summary>Retries a failed run, reusing the original user message. Returns null if there
    /// is nothing to retry.</summary>
    ConversationRunSnapshot? RetryRun(AgentConfig agent, string conversationId);

    /// <summary>
    /// Sends one fire-and-forget INTERRUPT on the active run's existing socket. Approval state
    /// does not change this global action; the approval card owns reject_hard separately.
    /// </summary>
    Task<StopOutcome> RequestStopAsync(string conversationId);

    /// <summary>
    /// Switches the conversation's approval mode. Applies to a turn in flight immediately (the
    /// agent picks it up at its next iteration boundary); with no turn running there is nothing
    /// on the host to tell, so the mode simply rides along with the next
    /// <see cref="SendMessageAsync"/> — which is why callers must keep passing it there.
    /// </summary>
    Task SetModeAsync(string conversationId, string mode);

    /// <summary>Forgets a resolved interactive turn, so Stop stops treating the agent as parked
    /// on it. Called by the page the moment it answers one.</summary>
    void ClearPendingInteractive(string conversationId);
}
