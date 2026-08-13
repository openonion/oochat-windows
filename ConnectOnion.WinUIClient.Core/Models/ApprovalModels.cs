namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// The five decisions an approval card offers. Kept as one enum (rather
/// than bool/scope/mode arguments at the call site) so the button, the accessibility name, the
/// submitting verb and the resolved status all switch on the same value.
/// </summary>
public enum ApprovalAction
{
    /// <summary>Approve just this call. Accent primary action.</summary>
    AllowOnce,
    /// <summary>Approve this tool for the rest of the session (the flyout's second row).</summary>
    TrustSession,
    /// <summary>Decline this one tool; the agent keeps working (soft reject).</summary>
    Reject,
    /// <summary>Halt the whole turn and hand control back (hard reject).</summary>
    Stop,
    /// <summary>Decline this tool and ask the agent to explain it.</summary>
    Explain,
}

/// <summary>
/// Lifecycle of the embedded approval region. Drives which face the unified tool-activity card
/// shows: the decision controls, an in-flight spinner, a compact resolved line, or a retryable
/// error. Distinct from <see cref="EventStatus"/> — that stays the persistence/record signal
/// (Running → Done/Error), while this is the richer live UI state.
/// </summary>
public enum ApprovalCardPhase
{
    /// <summary>Awaiting the user; the full decision UI is shown.</summary>
    Waiting,
    /// <summary>A decision was clicked and is being sent; controls are disabled to block a
    /// double-submit and a spinner shows the verb.</summary>
    Submitting,
    ApprovedOnce,
    TrustedSession,
    Rejected,
    Stopped,
    ExplanationRequested,
    /// <summary>Sending the decision threw; the controls come back with a retry.</summary>
    Failed,
}

/// <summary>What kind of thing an approval is acting on, so the card can pick an icon and verb.</summary>
public enum ApprovalTargetKind
{
    /// <summary>No specific target could be extracted — the card falls back to a generic prompt.</summary>
    None,
    File,
    Directory,
    Command,
    Url,
    /// <summary>A free-text target (a message body, a search string) — shown, but with a neutral icon.</summary>
    Text,
}

/// <summary>
/// The display model an approval card shows above its buttons: <i>which</i> tool wants to do
/// <i>what</i> to <i>which</i> target. Built by <c>ApprovalTargetFormatter</c> from the request's
/// arguments; the view owns the wording.
/// </summary>
/// <param name="Kind">What the target is, so the view picks the matching icon and verb.</param>
/// <param name="Target">The short label to show in the target chip (a file name, a host, a
/// truncated command). Empty when <paramref name="Kind"/> is <see cref="ApprovalTargetKind.None"/>.</param>
/// <param name="FullTarget">The untruncated value for the chip's tooltip (the whole path/URL).</param>
public readonly record struct ApprovalTarget(ApprovalTargetKind Kind, string Target, string FullTarget)
{
    public bool HasTarget => Kind != ApprovalTargetKind.None && Target.Length > 0;

    /// <summary>The verb fragment for "This tool wants to …:", chosen from the target kind. Kept
    /// here (not in XAML) so it stays testable and consistent with the icon.</summary>
    public string OperationVerb => Kind switch
    {
        ApprovalTargetKind.File => "modify",
        ApprovalTargetKind.Directory => "write to",
        ApprovalTargetKind.Command => "run the command",
        ApprovalTargetKind.Url => "reach",
        ApprovalTargetKind.Text => "send",
        _ => "proceed",
    };

    public static readonly ApprovalTarget Empty = new(ApprovalTargetKind.None, "", "");
}
