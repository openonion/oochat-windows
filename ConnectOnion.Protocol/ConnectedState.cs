using System;
using System.Text.Json;

namespace ConnectOnion.Protocol;

/// <summary>The host's verdict on the session named in our CONNECT frame.</summary>
public static class ConnectedStatuses
{
    /// <summary>An agent is executing this session right now. The host has already called
    /// <c>io.rewind_to(last_msg_id)</c> and resumed forwarding, so the events we missed are
    /// on their way — a client that reconnects into this state must replay them into the
    /// existing turn rather than starting a new one.</summary>
    public const string Running = "running";

    /// <summary>The session is alive and idle. Safe to send a new INPUT.</summary>
    public const string Connected = "connected";

    /// <summary>The host has never seen this session id.</summary>
    public const string New = "new";
}

/// <summary>
/// The parsed CONNECTED frame. The host puts three things in it that a bare "we're connected"
/// boolean throws away, and each one drives a different client decision:
/// <list type="bullet">
/// <item><see cref="Status"/> — whether a turn is already running (so we resume rather than
/// re-send), idle, or unknown.</item>
/// <item><see cref="ServerNewer"/> — the host's <c>merge_sessions</c> found its copy ahead of
/// ours and is handing back the authoritative one.</item>
/// <item><see cref="ChatItemsJson"/> — that session flattened for rendering, sent only
/// alongside <see cref="ServerNewer"/>.</item>
/// </list>
/// </summary>
/// <param name="SessionId">The session id the host settled on — ours if it accepted it, or one
/// it minted when we sent none.</param>
/// <param name="Status">One of <see cref="ConnectedStatuses"/>. Unknown values are passed
/// through verbatim rather than coerced, so a host that grows a fourth state is visible in logs
/// instead of being silently read as "new".</param>
/// <param name="ServerNewer">The host replaced our session with its own.</param>
/// <param name="SessionJson">The merged session, raw. Present only when <paramref name="ServerNewer"/>.</param>
/// <param name="ChatItemsJson">The merged session as a flat renderable list, raw. Present only
/// when <paramref name="ServerNewer"/>. Note the ids in it are <i>positional</i>
/// (<c>msg-0</c>, <c>msg-1</c>, …), not stable identifiers — see
/// <c>session_to_chat_items</c> in the host. They cannot be matched against our own message
/// ids, which is why this is a signal to reconcile and not a drop-in replacement for local
/// history.</param>
public sealed record ConnectedState(
    string? SessionId,
    string Status,
    bool ServerNewer,
    string? SessionJson,
    string? ChatItemsJson)
{
    public bool IsRunning => string.Equals(Status, ConnectedStatuses.Running, StringComparison.Ordinal);

    public static ConnectedState Parse(WireMessage msg)
    {
        var serverNewer = msg.GetBool("server_newer");
        return new ConnectedState(
            msg.GetString("session_id"),
            msg.GetString("status") ?? ConnectedStatuses.New,
            serverNewer,
            // Read the payloads only when the flag is set — the host sends them only alongside
            // it. Note that is NOT a rare case: merge_sessions compares iteration counts, we
            // report none, and the host's count climbs with every LLM call, so server_newer
            // comes back true on essentially every CONNECT to a session it has already run.
            // These two strings are therefore materialized on most reconnects, and their size
            // grows with the conversation. Anything added here is paid for per reconnect.
            serverNewer && msg.TryGet("session", out var session) ? session.GetRawText() : null,
            serverNewer && msg.TryGet("chat_items", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.GetRawText()
                : null);
    }
}

/// <summary>
/// Thrown by <c>ResumeRunningSessionAsync</c> when the host does not have an agent executing
/// the session we tried to rejoin. Distinct from a connection failure on purpose: the socket is
/// fine, there is simply nothing to attach to, and the caller's response is to settle the
/// abandoned turn rather than to retry.
/// </summary>
public sealed class SessionNotRunningException : Exception
{
    public SessionNotRunningException(string status)
        : base($"The host is not running this session (status: {status}).")
        => Status = status;

    /// <summary>The status the host reported instead of "running".</summary>
    public string Status { get; }
}

/// <summary>
/// Raised as the socket works its way back after an unexpected drop, so the UI can say which
/// of the two very different situations the user is in: "still trying" (their turn may yet
/// survive) versus "gave up" (it will not).
/// </summary>
/// <param name="Attempt">1-based attempt number; equals <see cref="ReconnectPolicy.MaxAttempts"/>
/// on the last one.</param>
/// <param name="Delay">How long we are waiting before this attempt.</param>
/// <param name="Error">What killed the previous connection.</param>
public sealed record ReconnectingEvent(int Attempt, TimeSpan Delay, Exception Error);
