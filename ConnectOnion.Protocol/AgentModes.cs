namespace ConnectOnion.Protocol;

/// <summary>
/// The approval modes the host's <c>tool_approval</c> plugin understands, and the wire
/// vocabulary around them.
///
/// The host validates against exactly this set (<c>VALID_MODES</c> in
/// <c>useful_plugins/tool_approval/constants.py</c>) and silently ignores anything else,
/// so an unknown string is a no-op rather than an error — which is why
/// <see cref="IsValid"/> exists and why the picker only ever offers these three.
///
/// <b>ULW is deliberately not here.</b> The host's <c>ulw</c> plugin does accept
/// <c>{ type: "mode_change", mode: "ulw", turns: N }</c>, but when its turn budget runs out
/// the agent emits <c>ulw_turns_reached</c> and then <b>blocks on <c>io.receive()</c></b>
/// waiting for a <c>ULW_RESPONSE</c>. Offering the mode without also shipping that
/// checkpoint card would hang the turn, so the mode stays out until the card exists.
/// </summary>
public static class AgentModes
{
    /// <summary>Dangerous tools need per-call approval. The host's default.</summary>
    public const string Safe = "safe";

    /// <summary>File edits (write/edit/multi_edit) are auto-approved; other dangerous tools
    /// still prompt.</summary>
    public const string AcceptEdits = "accept_edits";

    /// <summary>Read-only tools only. The agent researches, writes a plan, and surfaces it as a
    /// <c>plan_review</c> turn before it is allowed to implement anything.</summary>
    public const string Plan = "plan";

    public static bool IsValid(string? mode)
        => mode is Safe or AcceptEdits or Plan;

    /// <summary>Returns the next mode in the same order as the composer's picker.</summary>
    public static string Next(string? mode) => mode switch
    {
        Safe => AcceptEdits,
        AcceptEdits => Plan,
        Plan => Safe,
        _ => Safe,
    };

    /// <summary>Short label for the composer's mode pill.</summary>
    public static string DisplayName(string? mode) => mode switch
    {
        AcceptEdits => "Accept edits",
        Plan => "Plan",
        _ => "Safe",
    };
}

/// <summary>
/// How the host should treat a rejected <c>approval_needed</c> turn. This is the
/// <c>mode</c> field of an <c>APPROVAL_RESPONSE</c>, and it is the <b>only</b> lever the
/// protocol gives a client for actually halting a running agent:
/// <see cref="Hard"/> sets <c>session['stop_signal']</c> host-side, which rejects the rest of
/// the tool batch and breaks the agent's iteration loop, ending the turn with an OUTPUT.
/// The other two let the loop keep going.
/// </summary>
public static class ApprovalRejectModes
{
    /// <summary>Skip the remaining batch, break the loop, wait for the user. The host's own
    /// default, and what the Stop button sends when an approval is pending.</summary>
    public const string Hard = "reject_hard";

    /// <summary>Skip just this tool; the agent keeps working and is hinted to offer alternatives.</summary>
    public const string Soft = "reject_soft";

    /// <summary>Like <see cref="Soft"/>, but the agent is instructed to explain the step in plain
    /// language before retrying.</summary>
    public const string Explain = "reject_explain";
}

/// <summary>
/// A <c>mode_changed</c> frame. The agent sends this both when it accepts a client's
/// <c>mode_change</c> (<c>triggered_by == "user"</c>) and when it switches modes on its own —
/// calling <c>enter_plan_mode()</c> emits <c>triggered_by == "agent"</c> — so the client must
/// treat the agent as the source of truth and follow it, not just echo its own selection.
/// </summary>
/// <param name="Mode">One of <see cref="AgentModes"/>, or an unknown mode from a newer host.</param>
/// <param name="TriggeredBy">"user", "agent", "ulw_checkpoint", or absent.</param>
public sealed record ModeChangedEvent(string Mode, string? TriggeredBy);

/// <summary>The host's answer to a <c>SESSION_STATUS</c> query — what the agent registry
/// currently thinks of a session.</summary>
public static class SessionStatuses
{
    /// <summary>An agent thread is executing this session right now. A fresh INPUT would be
    /// routed to it as mid-execution <c>RUNTIME_INPUT</c>, not start a new turn.</summary>
    public const string Running = "running";

    /// <summary>Known, idle, ready for a new turn.</summary>
    public const string Connected = "connected";

    /// <summary>The host has no record of the session (it will be created on the next INPUT).</summary>
    public const string NotFound = "not_found";
}
