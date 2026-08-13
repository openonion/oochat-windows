namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// What the conversation's WebSocket is doing right now, as shown under the composer.
///
/// <para>This is the <i>transport's</i> state, which is not the same thing as the run's state
/// and is why it gets its own type rather than reusing <c>ConversationRunStatus</c>. A turn
/// parked on an approval is <c>Running</c> as far as the run registry is concerned — the agent
/// still owns the turn — but the interesting fact for the user at that moment is that the socket
/// is open and healthy and the agent is waiting on <i>them</i>. Equally, <see cref="Reconnecting"/>
/// has no run-status equivalent at all: the run is still Running while the socket underneath it
/// is being rebuilt.</para>
/// </summary>
public enum ConnectionPhase
{
    /// <summary>No socket. Normal for a conversation that has not been used this session —
    /// connections are opened on first send, not on open.</summary>
    Idle,

    /// <summary>Opening the socket and running the CONNECT handshake.</summary>
    Connecting,

    /// <summary>Socket open and authenticated, no turn in flight.</summary>
    Connected,

    /// <summary>A turn is streaming.</summary>
    Running,

    /// <summary>The agent is blocked on a human answer (ask_user / approval / plan_review).
    /// The socket is fine; nothing will move until the user acts.</summary>
    Waiting,

    /// <summary>The socket dropped mid-turn and is being rebuilt with backoff. Carries the
    /// attempt number so the UI can say how many tries are left before the turn is lost.</summary>
    Reconnecting,

    /// <summary>The agent is unreachable — either presence says so, or reconnect gave up.</summary>
    Offline,
}
