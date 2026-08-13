using System;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Services.Runtime;

namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// <see cref="ChatMessage"/>: the state and commands for an <c>approval_needed</c> card, which owns
/// its own transcript row alongside ask_user and plan_review (<c>ApprovalCard.xaml</c>).
///
/// <para>It was previously drawn <i>inside</i> the turn's tool-activity card. That card is a
/// turn-level aggregate anchored at the turn's <b>first</b> tool call, while an approval arrives
/// much later — so a turn that had also appended a plan review, a question or a couple of diff
/// previews drew the live decision back up above all of them, stranded mid-conversation with
/// settled cards on either side. <c>ToolActivityViewModel.Approval</c> survives as a
/// back-reference: it is what puts "Approval required" on the tool card's header and folds its
/// timeline while the decision is open.</para>
///
/// <para>The network round-trip lives in the view model (it needs the live socket); this partial
/// only holds the display projection and the phase machine, and routes a clicked decision to a
/// <see cref="ApprovalResponder"/> the view model sets. The commands flip to
/// <see cref="ApprovalCardPhase.Submitting"/> <i>synchronously</i> before awaiting, so a double-click
/// or an Enter-repeat cannot send twice.</para>
/// </summary>
public sealed partial class ChatMessage
{
    // ---- Request display (populated once by the projection) ----

    /// <summary>The extracted target/operation for the approval summary, built by
    /// <c>ApprovalTargetFormatter</c> from the request's arguments. The card renders this, never the
    /// raw <see cref="EventArgs"/> blob (which stays behind "View operation details").</summary>
    [JsonIgnore]
    public ApprovalTarget ApprovalTargetInfo { get; set; } = ApprovalTarget.Empty;

    [JsonIgnore] public bool HasApprovalTarget => ApprovalTargetInfo.HasTarget;
    [JsonIgnore] public string ApprovalTargetText => ApprovalTargetInfo.Target;
    [JsonIgnore] public string ApprovalTargetTooltip => ApprovalTargetInfo.FullTarget;
    [JsonIgnore] public ApprovalTargetKind ApprovalTargetKind => ApprovalTargetInfo.Kind;
    [JsonIgnore] public bool IsApprovalCommand => ApprovalTargetKind == ApprovalTargetKind.Command;
    [JsonIgnore] public bool ShowApprovalNonCommandTarget => HasApprovalTarget && !IsApprovalCommand;
    [JsonIgnore] public string ApprovalCommandText => IsApprovalCommand ? ApprovalTargetInfo.FullTarget : "";
    [JsonIgnore] public bool HasApprovalCommand => ApprovalCommandText.Length > 0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApprovalCommandMaxLines))]
    [NotifyPropertyChangedFor(nameof(ApprovalCommandToggleLabel))]
    [JsonIgnore]
    public partial bool IsApprovalCommandExpanded { get; set; }

    [JsonIgnore] public int ApprovalCommandMaxLines => IsApprovalCommandExpanded ? 0 : 3;
    [JsonIgnore]
    public string ApprovalCommandToggleLabel => IsApprovalCommandExpanded
        ? CoreStrings.Get("ApprovalCollapseCommand", "Collapse command")
        : CoreStrings.Get("ApprovalShowFullCommand", "Show full command");

    [RelayCommand]
    private void ToggleApprovalCommand() => IsApprovalCommandExpanded = !IsApprovalCommandExpanded;

    /// <summary>Set by the projection to mark an approval that belongs to a tool-activity card.
    ///
    /// <para>No longer a rendering switch — the approval draws its own row either way. It is the
    /// signal the view model uses to decide an approval answers through the live socket, so it is
    /// what gates wiring <c>ApprovalResponder</c> at add time.</para></summary>
    [JsonIgnore]
    public bool IsEmbeddedApproval { get; set; }

    /// <summary>The tool-activity card this approval is embedded in, so the view model can clear the
    /// card's parked "waiting" status the moment a decision is sent (before the turn's own Complete
    /// runs). Set by the projection; <c>[JsonIgnore]</c> — approvals aren't persisted.</summary>
    [JsonIgnore]
    public ToolActivityViewModel? OwnerToolActivity { get; set; }

    [JsonIgnore]
    public string ApprovalPromptTitle => IsApprovalCommand
        ? CoreStrings.Get("ApprovalTitleCommand", "Approve command execution?")
        : ApprovalTargetKind == ApprovalTargetKind.File
            ? CoreStrings.Get("ApprovalTitleFile", "Approve file operation?")
            : CoreStrings.Get("ApprovalTitleGeneric", "Approve this operation?");

    /// <summary>Empty for a command approval, and that is the fix rather than an omission.
    ///
    /// <para>The card was stating one fact four times: the header badge ("Approval required"), the
    /// prompt title ("Approve command execution?"), this subtitle ("This command can modify or
    /// delete files on the remote server.") and the risk line ("Risk: This command may delete or
    /// modify server data."). The risk line is now derived from the actual command and is the only
    /// one of the four that carries information, so the subtitle — which was the same sentence in
    /// weaker words — stands down and the user reads the command sooner.</para></summary>
    [JsonIgnore]
    public string ApprovalPromptSubtitle => IsApprovalCommand
        ? ""
        : CoreStrings.Get(
            "ApprovalSubtitleGeneric",
            "Review the operation and its risk before allowing it to continue.");

    [JsonIgnore] public bool HasApprovalPromptSubtitle => ApprovalPromptSubtitle.Length > 0;

    /// <summary>"This tool wants to modify:" — the operation line above the target chip. Empty when
    /// no target could be extracted, in which case <see cref="ShowApprovalFallback"/> is shown.</summary>
    [JsonIgnore]
    public string ApprovalOperationLine => HasApprovalTarget
        ? $"This tool wants to {ApprovalTargetInfo.OperationVerb}:"
        : "";

    /// <summary>The generic line shown when no concrete target is available.</summary>
    [JsonIgnore]
    public string ApprovalFallbackLine => CoreStrings.Get(
        "ApprovalFallbackLine",
        "This operation requires your approval before it can continue.");

    [JsonIgnore] public bool ShowApprovalFallback => !HasApprovalTarget;

    /// <summary>Compact preview of the remaining tools in the same approval batch.</summary>
    [JsonIgnore] public string ApprovalBatchSummary { get; set; } = "";
    [JsonIgnore] public bool HasApprovalBatch => !string.IsNullOrWhiteSpace(ApprovalBatchSummary);

    // ---- "Explain this risk" / "View operation details" (local disclosure) ----

    private bool _approvalDetailsOpen;

    /// <summary>Whether the "View operation details" panel (the formatted arguments) is open.
    /// Purely local — it reveals what the agent already sent and never answers the approval.</summary>
    [JsonIgnore] public bool IsApprovalDetailsOpen => _approvalDetailsOpen;

    [RelayCommand]
    private void ToggleApprovalDetails()
    {
        _approvalDetailsOpen = !_approvalDetailsOpen;
        OnPropertyChanged(nameof(IsApprovalDetailsOpen));
        OnPropertyChanged(nameof(ApprovalDetailsLabel));
        OnPropertyChanged(nameof(ApprovalDetailsChevronAngle));
    }

    [JsonIgnore]
    public string ApprovalDetailsLabel => IsApprovalDetailsOpen
        ? CoreStrings.Get("ApprovalHideDetails", "Hide operation details")
        : CoreStrings.Get("ApprovalShowDetails", "Operation details");

    /// <summary>0 points the chevron down when open, 270 points it right when closed — the same
    /// direction every other disclosure in the app turns (<c>InteractiveCard.ChevronAngle</c>,
    /// <c>ToolActivityViewModel.ChevronAngle</c>, the sidebar's agent rows).</summary>
    [JsonIgnore] public double ApprovalDetailsChevronAngle => IsApprovalDetailsOpen ? 0 : 270;

    /// <summary>"Explain this risk" reuses the same local disclosure as the risk description. It
    /// answers nothing — inspecting a request never costs the user their decision.</summary>
    [RelayCommand]
    private void ExplainApprovalRisk() => ToggleApprovalExplain();

    /// <summary>The text the "Explain this risk" panel shows: the agent's own description when it
    /// sent one, otherwise a generic caution so the link is never a dead end (an approval frequently
    /// carries no description, and a link that reveals nothing reads as broken).</summary>
    [JsonIgnore]
    public string ApprovalRiskText => ApprovalRisk switch
    {
        CommandRisk.Destructive => CoreStrings.Get(
            "ApprovalRiskCommand", "Risk: This command may delete or modify server data."),
        CommandRisk.ReadOnly => CoreStrings.Get(
            "ApprovalRiskReadOnly", "This command only reads. Nothing is changed or deleted."),
        _ when IsApprovalCommand => CoreStrings.Get(
            "ApprovalRiskUnknown",
            "This command was not recognised. Read it before allowing it to run."),
        _ => CoreStrings.Get(
            "ApprovalRiskGeneric", "Risk: This operation may change remote server data."),
    };

    /// <summary>What the command actually does, derived from the command itself rather than
    /// asserted for every approval alike. See <see cref="CommandRiskAssessor"/> for why the
    /// unrecognised case is a caution and never an all-clear.
    ///
    /// <para>A non-command approval (a file write, a URL fetch) has no command string to read, so
    /// it stays <see cref="CommandRisk.Unknown"/> and keeps the generic wording.</para></summary>
    [JsonIgnore]
    public CommandRisk ApprovalRisk => IsApprovalCommand
        ? CommandRiskAssessor.Assess(ApprovalCommandText)
        : CommandRisk.Unknown;

    /// <summary>Tone for the risk block and the decision buttons. A read-only command gets no
    /// alarm colouring at all — painting one amber trains the user to ignore the amber that
    /// matters.</summary>
    [JsonIgnore]
    public InteractiveVisualTone ApprovalRiskTone => ApprovalRisk switch
    {
        CommandRisk.Destructive => InteractiveVisualTone.Danger,
        CommandRisk.ReadOnly => InteractiveVisualTone.Success,
        _ => InteractiveVisualTone.Warning,
    };

    [JsonIgnore]
    public string ApprovalRiskGlyph => ApprovalRisk switch
    {
        CommandRisk.Destructive => "Warning",
        CommandRisk.ReadOnly => "Checkmark",
        _ => "Info",
    };

    /// <summary>Whether allowing this is the emphasised action.
    ///
    /// <para>False for a destructive command, and that inversion is the point: the accent-filled,
    /// last-in-tab-order button under a red risk warning used to be <b>Allow once</b>, so the
    /// card's whole visual language said "go" at exactly the moment it was asking the user to
    /// stop. When the command is destructive the emphasis moves to Decline and Allow becomes an
    /// ordinary button — still one click, just no longer the one the eye lands on.</para></summary>
    [JsonIgnore]
    public bool IsAllowTheSafeChoice => ApprovalRisk != CommandRisk.Destructive;

    [JsonIgnore]
    public string ApprovalAllowButtonStyle => IsAllowTheSafeChoice
        ? "InteractiveCardPrimaryButtonStyle"
        : "InteractiveCardButtonStyle";

    [JsonIgnore]
    public string ApprovalDeclineButtonStyle => IsAllowTheSafeChoice
        ? "InteractiveCardButtonStyle"
        : "InteractiveCardPrimaryButtonStyle";

    /// <summary>The ▶ play glyph is dropped on a destructive command for the same reason the accent
    /// fill is: it is an invitation to run.</summary>
    [JsonIgnore]
    public bool ShowApprovalAllowGlyph => IsAllowTheSafeChoice;

    /// <summary>The panel is shown whenever explain is open — it always has text now (see
    /// <see cref="ApprovalRiskText"/>). <see cref="ToggleApprovalExplain"/> raises this so the panel
    /// actually appears when the link is clicked.</summary>
    [JsonIgnore] public bool ShowApprovalRisk => IsApprovalExplainOpen;

    // ---- Phase machine ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApprovalWaiting))]
    [NotifyPropertyChangedFor(nameof(IsApprovalSubmitting))]
    [NotifyPropertyChangedFor(nameof(AreApprovalRequestControlsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsApprovalPending))]
    [NotifyPropertyChangedFor(nameof(IsApprovalResolved))]
    [NotifyPropertyChangedFor(nameof(IsApprovalApprovedResult))]
    [NotifyPropertyChangedFor(nameof(IsApprovalRejectedResult))]
    [NotifyPropertyChangedFor(nameof(IsApprovalStoppedResult))]
    [NotifyPropertyChangedFor(nameof(IsApprovalExplanationResult))]
    [NotifyPropertyChangedFor(nameof(IsApprovalFailedResult))]
    [NotifyPropertyChangedFor(nameof(ShowApprovalRequestBody))]
    [NotifyPropertyChangedFor(nameof(IsApprovalActionable))]
    [NotifyPropertyChangedFor(nameof(ApprovalResolvedText))]
    [NotifyCanExecuteChangedFor(nameof(AllowOnceCommand))]
    [NotifyCanExecuteChangedFor(nameof(TrustSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(RejectCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExplainCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryApprovalCommand))]
    [JsonIgnore]
    public partial ApprovalCardPhase ApprovalPhase { get; set; }

    // The standalone card's chrome is a switch over the phase, so it repaints from here. Ten
    // OnPropertyChanged calls in one place rather than ten more [NotifyPropertyChangedFor]
    // attributes on an already twenty-attribute property.
    partial void OnApprovalPhaseChanged(ApprovalCardPhase value)
        => NotifyApprovalCardChromeChanged();

    /// <summary>The decision currently being sent, so the spinner shows the right verb and Retry
    /// re-issues the same action.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApprovalSubmittingVerb))]
    [JsonIgnore]
    public partial ApprovalAction ApprovalPendingAction { get; set; }

    [ObservableProperty]
    [JsonIgnore]
    public partial string? ApprovalErrorText { get; set; }

    [JsonIgnore] public bool IsApprovalWaiting => ApprovalPhase == ApprovalCardPhase.Waiting;
    [JsonIgnore] public bool IsApprovalSubmitting => ApprovalPhase == ApprovalCardPhase.Submitting;
    [JsonIgnore] public bool AreApprovalRequestControlsEnabled => !IsApprovalSubmitting;
    /// <summary>Still blocking the turn (awaiting the user or mid-send), so the card's amber
    /// "Waiting for approval" status stays lit until a decision resolves.</summary>
    [JsonIgnore]
    public bool IsApprovalPending
        => ApprovalPhase is ApprovalCardPhase.Waiting or ApprovalCardPhase.Submitting
            or ApprovalCardPhase.Failed;
    [JsonIgnore] public bool IsApprovalFailedResult => ApprovalPhase == ApprovalCardPhase.Failed;
    [JsonIgnore]
    public bool IsApprovalApprovedResult
        => ApprovalPhase is ApprovalCardPhase.ApprovedOnce or ApprovalCardPhase.TrustedSession;
    [JsonIgnore] public bool IsApprovalRejectedResult => ApprovalPhase == ApprovalCardPhase.Rejected;
    [JsonIgnore] public bool IsApprovalStoppedResult => ApprovalPhase == ApprovalCardPhase.Stopped;
    [JsonIgnore] public bool IsApprovalExplanationResult => ApprovalPhase == ApprovalCardPhase.ExplanationRequested;
    [JsonIgnore]
    public bool IsApprovalResolved
        => ApprovalPhase is ApprovalCardPhase.ApprovedOnce or ApprovalCardPhase.TrustedSession
            or ApprovalCardPhase.Rejected or ApprovalCardPhase.Stopped
            or ApprovalCardPhase.ExplanationRequested;

    /// <summary>The request summary (title / operation / target / disclosure) shows until a decision
    /// resolves — through the submitting and failed phases too, so the user keeps their context.</summary>
    [JsonIgnore] public bool ShowApprovalRequestBody => !IsApprovalResolved;

    // ---- Standalone card chrome ---------------------------------------------
    // An approval used to be rendered inside the turn's tool-activity card. That card is a
    // turn-level aggregate anchored at the turn's FIRST tool call, while an approval is a moment
    // that arrives much later — so once the turn had also appended a plan, a question or a couple
    // of diffs, the decision was drawn back up above all of them, stranded mid-conversation with
    // settled cards on both sides. It is now its own row, arriving where it happened, and these
    // mirror the AskUser*/Plan* chrome the other interactive cards already had.

    /// <summary>The tool being approved while the decision is open; its outcome once made.</summary>
    [JsonIgnore]
    public string ApprovalCardTitle
        => IsApprovalResolved ? ApprovalResolvedText : EventTitle;

    /// <summary>The question, under the tool's name. Empty once settled — the title has become the
    /// answer, and restating "Approve command execution?" beneath it would ask again.</summary>
    [JsonIgnore]
    public string ApprovalCardSubtitle
        => IsApprovalResolved ? "" : ApprovalPromptTitle;

    [JsonIgnore] public bool ShowApprovalStatusBadge => !IsApprovalResolved;

    [JsonIgnore]
    public string ApprovalStatusLabel => ApprovalPhase switch
    {
        ApprovalCardPhase.Submitting => CoreStrings.Get("InteractiveStateSubmitting", "Submitting"),
        ApprovalCardPhase.Failed => CoreStrings.Get("InteractiveStateError", "Try again"),
        _ => CoreStrings.Get("ToolApprovalRequiredBadge", "Approval required"),
    };

    /// <summary>The rail follows the command's real risk while the decision is open, and stands
    /// down once it is made — a settled card says what it is through its icon and title.</summary>
    [JsonIgnore]
    public InteractiveVisualTone ApprovalCardChromeTone
        => IsApprovalResolved ? InteractiveVisualTone.Neutral : ApprovalRiskTone;

    [JsonIgnore]
    public InteractiveVisualTone ApprovalCardIconTone => ApprovalPhase switch
    {
        ApprovalCardPhase.ApprovedOnce or ApprovalCardPhase.TrustedSession
            => InteractiveVisualTone.Success,
        ApprovalCardPhase.Rejected or ApprovalCardPhase.Stopped => InteractiveVisualTone.Danger,
        ApprovalCardPhase.ExplanationRequested => InteractiveVisualTone.Neutral,
        ApprovalCardPhase.Failed => InteractiveVisualTone.Danger,
        _ => ApprovalRiskTone,
    };

    /// <summary>Shield / check / blocked, matching the tone. Segoe Fluent Icons codepoints, the
    /// same family <c>AskUserIconGlyph</c> and <c>PlanIconGlyph</c> draw from.</summary>
    [JsonIgnore]
    public string ApprovalCardIconGlyph => ApprovalPhase switch
    {
        ApprovalCardPhase.ApprovedOnce or ApprovalCardPhase.TrustedSession => "",
        ApprovalCardPhase.Rejected or ApprovalCardPhase.Stopped => "",
        _ => "",
    };

    /// <summary>A resolved approval folds to its header. There is nothing left to read: the
    /// request body is gone (<see cref="ShowApprovalRequestBody"/>) and the outcome is the title.</summary>
    [JsonIgnore] public bool ShowApprovalCardBody => !IsApprovalResolved;

    [JsonIgnore] public bool IsApprovalCardCompact => IsApprovalResolved;

    [JsonIgnore]
    public string ApprovalAccessibilityName => IsApprovalResolved
        ? $"{EventTitle}. {ApprovalResolvedText}."
        : $"{ApprovalPromptTitle} {EventTitle}. {ApprovalRiskText}";

    private void NotifyApprovalCardChromeChanged()
    {
        OnPropertyChanged(nameof(ApprovalCardTitle));
        OnPropertyChanged(nameof(ApprovalCardSubtitle));
        OnPropertyChanged(nameof(ShowApprovalStatusBadge));
        OnPropertyChanged(nameof(ApprovalStatusLabel));
        OnPropertyChanged(nameof(ApprovalCardChromeTone));
        OnPropertyChanged(nameof(ApprovalCardIconTone));
        OnPropertyChanged(nameof(ApprovalCardIconGlyph));
        OnPropertyChanged(nameof(ShowApprovalCardBody));
        OnPropertyChanged(nameof(IsApprovalCardCompact));
        OnPropertyChanged(nameof(ApprovalAccessibilityName));
    }

    /// <summary>The decision buttons are shown while awaiting the user or after a failed submit
    /// (which offers a Retry); they give way to a spinner while submitting and to the resolved line
    /// once decided.</summary>
    [JsonIgnore]
    public bool IsApprovalActionable
        => ApprovalPhase is ApprovalCardPhase.Waiting or ApprovalCardPhase.Failed;

    /// <summary>The compact resolved line inside the card once a decision is made.</summary>
    [JsonIgnore]
    public string ApprovalResolvedText => ApprovalPhase switch
    {
        ApprovalCardPhase.ApprovedOnce => CoreStrings.Get("ApprovalResolvedOnce", "Approved once"),
        ApprovalCardPhase.TrustedSession
            => CoreStrings.Get("ApprovalResolvedSession", "Allowed for this session"),
        ApprovalCardPhase.Rejected => CoreStrings.Get("ApprovalResolvedDeclined", "Declined"),
        ApprovalCardPhase.Stopped => CoreStrings.Get("ApprovalResolvedStopped", "Task stopped"),
        ApprovalCardPhase.ExplanationRequested
            => CoreStrings.Get("ApprovalResolvedExplain", "Explanation requested"),
        _ => "",
    };

    [JsonIgnore]
    public string ApprovalSubmittingVerb => ApprovalPendingAction switch
    {
        ApprovalAction.Reject => CoreStrings.Get("ApprovalSubmittingReject", "Rejecting…"),
        ApprovalAction.Stop => CoreStrings.Get("ApprovalSubmittingStop", "Stopping…"),
        ApprovalAction.Explain
            => CoreStrings.Get("ApprovalSubmittingExplain", "Requesting explanation…"),
        _ => CoreStrings.Get("ApprovalSubmittingApprove", "Approving…"),
    };

    [ObservableProperty]
    [JsonIgnore]
    public partial string ApprovalFeedback { get; set; }

    public void CloseApprovalDisclosures()
    {
        if (_approvalDetailsOpen)
        {
            _approvalDetailsOpen = false;
            OnPropertyChanged(nameof(IsApprovalDetailsOpen));
            OnPropertyChanged(nameof(ApprovalDetailsLabel));
            OnPropertyChanged(nameof(ApprovalDetailsChevronAngle));
        }
        IsApprovalCommandExpanded = false;
        // Esc backing out of the stop confirmation is the safe direction, so it is included here.
        // Esc is deliberately NOT wired to any decision: Decline is still an answer sent to the
        // agent, and a key people press reflexively to dismiss things must not answer for them.
        IsApprovalStopConfirmOpen = false;
    }

    // ---- Decision routing ----

    /// <summary>Set by the live view model; performs the network round-trip for a decision. Null on
    /// the headless persist path, where the decision controls never render.</summary>
    private Func<ChatMessage, ApprovalAction, Task>? _approvalResponder;

    [JsonIgnore]
    public Func<ChatMessage, ApprovalAction, Task>? ApprovalResponder
    {
        get => _approvalResponder;
        set
        {
            if (ReferenceEquals(_approvalResponder, value)) return;
            _approvalResponder = value;

            // The live projection adds the approval to the Tool Activity before its backing
            // message reaches IChatProjectionTarget.Add, where the responder is wired. XAML can
            // therefore query CanExecute once while this is still null. Commands do not
            // automatically observe a delegate property, so explicitly invalidate all of them
            // when the live responder arrives (or is detached during cleanup).
            AllowOnceCommand.NotifyCanExecuteChanged();
            TrustSessionCommand.NotifyCanExecuteChanged();
            RejectCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            ExplainCommand.NotifyCanExecuteChanged();
            RetryApprovalCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Actionable while awaiting the user, or after a failed submit (so Retry works). The
    /// commands' <c>CanExecute</c>, so a rebuilt view re-queries it on <see cref="ApprovalPhase"/>
    /// changes.</summary>
    private bool CanRunApproval => ApprovalResponder is not null
        && ApprovalPhase is ApprovalCardPhase.Waiting or ApprovalCardPhase.Failed;

    [RelayCommand(CanExecute = nameof(CanRunApproval))]
    private Task AllowOnce() => RespondApproval(ApprovalAction.AllowOnce);

    [RelayCommand(CanExecute = nameof(CanRunApproval))]
    private Task TrustSession() => RespondApproval(ApprovalAction.TrustSession);

    [RelayCommand(CanExecute = nameof(CanRunApproval))]
    private Task Reject() => RespondApproval(ApprovalAction.Reject);

    /// <summary>Opens the confirmation instead of stopping.
    ///
    /// <para>Stop ends the whole turn, not just this command — the single most destructive action
    /// on the card — and it used to fire on one click from a control styled exactly like "Copy
    /// command" sitting next to "Explain why". <c>plan_review</c> already gates its far milder
    /// Reject behind a confirmation (<c>IsPlanRejectConfirmOpen</c>); this brings the more
    /// dangerous action up to the same bar rather than leaving the two inconsistent.</para></summary>
    [RelayCommand(CanExecute = nameof(CanRunApproval))]
    private void Stop() => IsApprovalStopConfirmOpen = true;

    [RelayCommand(CanExecute = nameof(CanRunApproval))]
    private Task ConfirmApprovalStop()
    {
        IsApprovalStopConfirmOpen = false;
        return RespondApproval(ApprovalAction.Stop);
    }

    [RelayCommand]
    private void CancelApprovalStop() => IsApprovalStopConfirmOpen = false;

    /// <summary>Whether the "this stops everything" confirmation is showing. Live-only, like every
    /// other disclosure on this card.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApprovalDecisionActions))]
    [JsonIgnore]
    public partial bool IsApprovalStopConfirmOpen { get; set; }

    /// <summary>The three ordinary decisions hide while the stop confirmation is up, so the card
    /// asks one question at a time.</summary>
    [JsonIgnore]
    public bool ShowApprovalDecisionActions => !IsApprovalStopConfirmOpen;

    [RelayCommand(CanExecute = nameof(CanRunApproval))]
    private Task Explain()
    {
        // Deliberately NOT localized: this is the feedback field of the wire frame, read by the
        // agent's model, not shown to the user. Translating it would ask a remote agent to
        // reason in whatever language the client happens to be set to.
        ApprovalFeedback = "Explain why this operation is required, what it will do, and what risks it has.";
        return RespondApproval(ApprovalAction.Explain);
    }

    [RelayCommand(CanExecute = nameof(CanRunApproval))]
    private Task RetryApproval() => RespondApproval(ApprovalPendingAction);

    /// <summary>Flips to <see cref="ApprovalCardPhase.Submitting"/> before awaiting, so a second rapid
    /// click sees the guard closed and cannot send twice; then hands off to the view model.</summary>
    private Task RespondApproval(ApprovalAction action)
    {
        if (ApprovalResponder is not { } responder) return Task.CompletedTask;
        if (ApprovalPhase is not (ApprovalCardPhase.Waiting or ApprovalCardPhase.Failed)) return Task.CompletedTask;

        ApprovalPendingAction = action;
        ApprovalErrorText = null;
        ApprovalPhase = ApprovalCardPhase.Submitting;
        return responder(this, action);
    }

    /// <summary>Called by the view model once a decision has been sent successfully, mapping the
    /// action to its resolved phase.</summary>
    public void CompleteApproval(ApprovalAction action)
        => ApprovalPhase = action switch
        {
            ApprovalAction.AllowOnce => ApprovalCardPhase.ApprovedOnce,
            ApprovalAction.TrustSession => ApprovalCardPhase.TrustedSession,
            ApprovalAction.Reject => ApprovalCardPhase.Rejected,
            ApprovalAction.Stop => ApprovalCardPhase.Stopped,
            ApprovalAction.Explain => ApprovalCardPhase.ExplanationRequested,
            _ => ApprovalCardPhase.ApprovedOnce,
        };

    /// <summary>Called by the view model when sending the decision threw: the controls come back
    /// with the error and a Retry, so the UI never stalls on "Approving…".</summary>
    public void FailApproval(string message)
    {
        ApprovalErrorText = message;
        ApprovalPhase = ApprovalCardPhase.Failed;
    }

    // ---- Accessibility (screen readers read the tool + scope, never an icon alone) ----

    // These are AutomationProperties.Name values: user-visible text that a screen reader speaks,
    // localized like any other string. They are also the only thing a screen-reader user has to
    // tell the four decision buttons apart, so the tool name stays interpolated in — a
    // translation may move the placeholder but must not drop it.
    [JsonIgnore]
    public string ApprovalAllowOnceName
        => FormatWithTool("ApprovalAllowOnceName", "Allow {0} once");
    [JsonIgnore]
    public string ApprovalTrustName
        => FormatWithTool("ApprovalTrustName", "Trust {0} for this session");
    [JsonIgnore]
    public string ApprovalRejectName
        => FormatWithTool("ApprovalRejectName", "Decline {0}; the agent may continue");
    [JsonIgnore]
    public string ApprovalStopName => CoreStrings.Get(
        "ApprovalStopName", "Stop the entire agent task, not only this command");
    [JsonIgnore]
    public string ApprovalExplainName
        => CoreStrings.Get("ApprovalExplainName", "Explain approval risk");
    [JsonIgnore]
    public string ApprovalRequestExplanationName => CoreStrings.Get(
        "ApprovalRequestExplanationName",
        "Reject this operation and ask the agent to explain it");
    [JsonIgnore]
    public string ApprovalDetailsName
        => CoreStrings.Get("ApprovalDetailsName", "View operation details");

    private string FormatWithTool(string key, string fallbackFormat)
    {
        var format = CoreStrings.Get(key, fallbackFormat);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, EventTitle);
        }
        catch (FormatException)
        {
            // A malformed translation must not leave a decision button unlabelled.
            return string.Format(CultureInfo.InvariantCulture, fallbackFormat, EventTitle);
        }
    }
}
