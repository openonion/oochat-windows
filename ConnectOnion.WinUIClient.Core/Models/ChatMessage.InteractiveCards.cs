using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Common;

namespace ConnectOnion.WinUIClient.Models;

/// <summary><see cref="ChatMessage"/> presentation state shared by ask_user and plan_review.</summary>
public sealed partial class ChatMessage
{
    private const string DiffStatePrefix = "diff-state:";
    private DiffPreviewModel? _diffPreview;
    private DiffChangeState? _presentedDiffState;

    /// <summary>True once the user has opened or closed this diff by hand. State-driven default
    /// expansion stops applying from that moment. See <see cref="SynchronizeDiffPresentation"/>.</summary>
    private bool _diffExpansionOverridden;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractiveSubmitting))]
    [NotifyPropertyChangedFor(nameof(IsInteractiveEditable))]
    [NotifyPropertyChangedFor(nameof(InteractiveStateLabel))]
    [NotifyPropertyChangedFor(nameof(InteractiveStateDescription))]
    [NotifyPropertyChangedFor(nameof(ShowInteractiveError))]
    [JsonIgnore]
    public partial InteractiveCardPhase InteractivePhase { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInteractiveError))]
    [JsonIgnore]
    public partial string? InteractiveErrorText { get; set; }

    [ObservableProperty]
    [JsonIgnore]
    public partial bool IsPlanFeedbackOpen { get; set; }

    [ObservableProperty]
    [JsonIgnore]
    public partial bool IsPlanRejectConfirmOpen { get; set; }

    [JsonIgnore] public bool HasPlayedInteractiveEntrance { get; set; }

    /// <summary>Whether the surrounding <c>InteractiveCard</c> is unfolded.
    ///
    /// <para>Lives on the message, not on the control, for the same reason
    /// <c>ToolActivityViewModel.IsExpanded</c> does: the card sits in a virtualized
    /// <c>ListView</c>, so its container is recycled onto a different message as the user
    /// scrolls. Held as control state, folding one card would silently fold whichever card
    /// later reused that container.</para>
    ///
    /// <para><c>[JsonIgnore]</c> — this is a view preference for the current session, not part
    /// of what a conversation replays as. Defaults to true (see the constructor).</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InteractiveAccessibilityName))]
    [JsonIgnore]
    public partial bool IsInteractiveCardExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffViewportHeight))]
    [NotifyPropertyChangedFor(nameof(DiffExpandLabel))]
    [JsonIgnore]
    public partial bool IsDiffExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffWrapLabel))]
    [JsonIgnore]
    public partial bool IsDiffWrapped { get; set; }

    [JsonIgnore] public bool IsInteractiveSubmitting => InteractivePhase == InteractiveCardPhase.Submitting;
    [JsonIgnore]
    public bool IsInteractiveEditable => Status == EventStatus.Running
        && InteractivePhase is InteractiveCardPhase.Pending or InteractiveCardPhase.Error;
    [JsonIgnore] public bool ShowInteractiveError => !string.IsNullOrWhiteSpace(InteractiveErrorText);
    [JsonIgnore] public bool IsConnectionLost => InteractivePhase == InteractiveCardPhase.ConnectionLost;
    [JsonIgnore] public bool IsInteractiveReadOnly => !IsInteractiveEditable && !IsInteractiveSubmitting;
    [JsonIgnore] public bool ShowInteractiveResult => Status != EventStatus.Running;
    [JsonIgnore] public bool ShowInteractiveActions => Status == EventStatus.Running;
    [JsonIgnore]
    public bool ShowAskUserSkipAction
        => ShowInteractiveActions && !IsFileChangeApproval;
    [JsonIgnore] public bool ShowAskUserFields => Status == EventStatus.Running && HasAskUserFields;
    [JsonIgnore] public bool ShowAskUserPendingContent => Status == EventStatus.Running;

    /// <summary>Whether the card has a body at all — which is <b>not</b> the same as whether it is
    /// still waiting on the user.
    ///
    /// <para>This used to be <see cref="ShowAskUserPendingContent"/>, so an answered card reported
    /// "no body". <c>InteractiveCard.RefreshDisclosure</c> derives <c>CanCollapse</c> from
    /// <c>ShowBody</c>, so that also hid the chevron and took the header out of the tab order —
    /// and the question the user was actually answering, plus any image the agent attached to it
    /// (a QR code, a login screenshot), became unreachable forever, in history too. A settled card
    /// should fold, not erase itself.</para>
    ///
    /// <para>Re-expanding a settled card costs nothing extra: the three input blocks inside the
    /// body (fields, options, free text) are each already gated on
    /// <see cref="IsAwaitingResponse"/>, so what comes back is the question and its attachments —
    /// never a password box replayed after the fact.</para></summary>
    [JsonIgnore]
    public bool ShowAskUserBody
        => !string.IsNullOrWhiteSpace(EventTitle) || HasAttachments;
    [JsonIgnore] public bool ShowAskUserStatusBadge => Status == EventStatus.Running;
    [JsonIgnore] public bool IsAskUserCompact => Status != EventStatus.Running;
    [JsonIgnore]
    public InteractiveVisualTone AskUserChromeTone
        => Status == EventStatus.Running ? InteractiveVisualTone.Warning : InteractiveVisualTone.Neutral;
    [JsonIgnore]
    public InteractiveVisualTone AskUserIconTone => Status == EventStatus.Running
        ? InteractiveVisualTone.Warning
        : IsRejectedAskUserResult ? InteractiveVisualTone.Danger : InteractiveVisualTone.Success;
    [JsonIgnore]
    public string AskUserIconGlyph => Status == EventStatus.Running
        ? "\uE897" : IsRejectedAskUserResult ? "\uE711" : "\uE73E";

    // ---- plan_review chrome -------------------------------------------------
    // The plan card passed no tone at all, so every state rendered Neutral: an approved plan, a
    // rejected one and one still blocking the agent were visually identical, and the only signal
    // was a low-contrast grey badge. These mirror the ask_user tones above.

    /// <summary>Amber rail while the plan still blocks the agent; nothing once it is settled \u2014 a
    /// Neutral tone draws no rail at all (see <c>InteractiveCard.ShowChromeRail</c>).</summary>
    [JsonIgnore]
    public InteractiveVisualTone PlanChromeTone
        => Status == EventStatus.Running ? InteractiveVisualTone.Warning : InteractiveVisualTone.Neutral;

    /// <summary>Colours the icon and the status badge: amber while pending, red once rejected,
    /// green once approved. <see cref="EventStatus.Error"/> is how a rejected plan is stored \u2014
    /// the same fact <see cref="HistoricalInteractiveStateLabel"/> reads for its label.</summary>
    [JsonIgnore]
    public InteractiveVisualTone PlanIconTone => Status switch
    {
        EventStatus.Running => InteractiveVisualTone.Warning,
        EventStatus.Error => InteractiveVisualTone.Danger,
        _ => InteractiveVisualTone.Success,
    };

    /// <summary>Filled-checkmark / blocked / clock, matching the tone.</summary>
    [JsonIgnore]
    public string PlanIconGlyph => Status switch
    {
        EventStatus.Running => "\uE9D5",
        EventStatus.Error => "\uE711",
        _ => "\uE73E",
    };

    /// <summary>The header subtitle. Only meaningful while the agent is actually waiting \u2014 once
    /// the plan is settled it becomes empty and the row collapses, leaving the footer's state
    /// description as the single place the outcome is stated. It previously stayed on
    /// "The agent will not execute until you respond" forever, which a rejected card still
    /// claimed hours later.</summary>
    [JsonIgnore]
    public string PlanReviewSubtitle => Status == EventStatus.Running
        ? CoreStrings.Get("PlanReviewSubtitle", "The agent will not execute until you respond.")
        : "";

    private const int PlanPreviewMaxLines = 24;
    private const int PlanPreviewMaxCharacters = 6_000;

    /// <summary>A bounded transcript preview. The complete plan remains available in the section
    /// review dialog, so the conversation can own vertical scrolling without realizing an
    /// arbitrarily tall markdown document inside one list item.</summary>
    [JsonIgnore]
    public string PlanReviewPreview
    {
        get
        {
            var detail = EventDetail ?? string.Empty;
            if (detail.Length == 0) return string.Empty;
            var lines = detail.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var preview = string.Join('\n', lines.Take(PlanPreviewMaxLines));
            if (preview.Length > PlanPreviewMaxCharacters)
                preview = preview[..PlanPreviewMaxCharacters];
            return preview.TrimEnd();
        }
    }

    [JsonIgnore]
    public bool IsPlanReviewPreviewTruncated
        => !string.Equals(PlanReviewPreview, (EventDetail ?? string.Empty).TrimEnd(), StringComparison.Ordinal);
    [JsonIgnore] public bool IsFileChangeApproval => TryGetAskUserTargetPath(out _);
    [JsonIgnore] public string AskUserTargetPath => TryGetAskUserTargetPath(out var path) ? path : "";
    [JsonIgnore] public string AskUserTargetFileName => DiffPathFormatter.FileName(AskUserTargetPath);
    [JsonIgnore] public string AskUserContextPath => DiffPathFormatter.ContextPath(AskUserTargetPath);
    [JsonIgnore]
    public string AskUserDisplayQuestion => IsFileChangeApproval
        ? CoreStrings.Format(
            "AskUserApplyChangeQuestion", "Apply this change to {0}?", AskUserTargetFileName)
        : EventTitle;
    [JsonIgnore]
    public string AskUserCardTitle => Status == EventStatus.Running
        ? IsFileChangeApproval
            ? CoreStrings.Get("AskUserTitleApprovalRequired", "Approval required")
            : CoreStrings.Get("AskUserTitleNeedsInput", "Agent needs your input")
        : AskUserResolvedTitle;
    [JsonIgnore]
    public string AskUserCardSubtitle => Status == EventStatus.Running
        ? IsFileChangeApproval
            ? CoreStrings.Get(
                "AskUserSubtitleReview", "Review how the agent should continue.")
            : CoreStrings.Get(
                "AskUserSubtitleChoose", "Choose how the agent should continue.")
        : AskUserResolvedSubtitle;
    [JsonIgnore]
    public string AskUserSubmitLabel
    {
        get
        {
            if (IsInteractiveSubmitting)
                return CoreStrings.Get("InteractiveSubmitting", "Submitting…");

            // Match arms are the host's literal option text, so they stay English (see
            // AskUserOptionEntry.DisplayText); only the button wording is localized.
            return AskUserOptionEntries.FirstOrDefault(option => option.IsChecked)?.Text switch
            {
                "Yes, apply this change"
                    => CoreStrings.Get("AskUserSubmitApplyOnce", "Apply once"),
                "Yes to all (auto-approve)"
                    => CoreStrings.Get("AskUserSubmitApplyAll", "Apply all"),
                "No, reject and give feedback"
                    => CoreStrings.Get("AskUserSubmitReject", "Reject"),
                _ => CoreStrings.Get("AskUserSubmitDefault", "Submit"),
            };
        }
    }
    [JsonIgnore]
    public string PlanReviewSubmitLabel => IsInteractiveSubmitting
        ? CoreStrings.Get("InteractiveSubmitting", "Submitting…")
        : CoreStrings.Get("PlanReviewApprove", "Approve plan");

    // InteractiveOutcome is a stored marker ("Skipped"/"Rejected"), not display text — it is
    // written to event_meta and read back. Match on it in English; localize only the result.
    [JsonIgnore]
    public string AskUserResultTitle => InteractiveOutcome switch
    {
        "Skipped" => CoreStrings.Get("AskUserResultSkipped", "Question skipped"),
        "Rejected" => CoreStrings.Get("AskUserResultRejected", "Question rejected"),
        _ when HasInteractiveAnswer
            => CoreStrings.Get("AskUserResultSubmitted", "Response submitted"),
        _ => CoreStrings.Get("AskUserResultCompleted", "Request completed"),
    };

    [JsonIgnore]
    public string InteractiveStateLabel =>
        InteractivePhase == InteractiveCardPhase.Pending && Status != EventStatus.Running
            ? HistoricalInteractiveStateLabel
            : EffectiveInteractivePhase switch
            {
                InteractiveCardPhase.Pending => CoreStrings.Get("InteractiveStateWaiting", "Waiting"),
                InteractiveCardPhase.Submitting
                    => CoreStrings.Get("InteractiveStateSubmitting", "Submitting"),
                InteractiveCardPhase.Submitted
                    => CoreStrings.Get("InteractiveStateSubmitted", "Submitted"),
                InteractiveCardPhase.Completed
                    => CoreStrings.Get("InteractiveStateCompleted", "Completed"),
                InteractiveCardPhase.Cancelled
                    => CoreStrings.Get("InteractiveStateCancelled", "Cancelled"),
                InteractiveCardPhase.Rejected
                    => CoreStrings.Get("InteractiveStateRejected", "Rejected"),
                InteractiveCardPhase.Expired => CoreStrings.Get("InteractiveStateExpired", "Expired"),
                InteractiveCardPhase.ConnectionLost
                    => CoreStrings.Get("InteractiveStateConnectionLost", "Connection lost"),
                InteractiveCardPhase.Error => CoreStrings.Get("InteractiveStateError", "Try again"),
                _ => CoreStrings.Get("InteractiveStateWaiting", "Waiting"),
            };

    /// <summary>State label for a card reloaded from history, derived from the stored
    /// <c>event_meta</c>. Every match arm reads a persisted English marker — <c>"Skipped"</c>,
    /// <c>"Rejected"</c>, the <c>"Answered:"</c> prefix the persist path writes — so the left
    /// side must never be localized or a translated build stops recognising its own rows.</summary>
    private string HistoricalInteractiveStateLabel
        => EventMeta switch
        {
            "Skipped" => CoreStrings.Get("InteractiveStateSkipped", "Skipped"),
            "Rejected" => CoreStrings.Get("InteractiveStateRejected", "Rejected"),
            { } value when value.StartsWith("Answered:", StringComparison.Ordinal)
                => CoreStrings.Get("InteractiveStateSubmitted", "Submitted"),
            { } value when value.Contains("approved", StringComparison.OrdinalIgnoreCase)
                => CoreStrings.Get("InteractiveStateApproved", "Approved"),
            _ => Status == EventStatus.Error
                ? CoreStrings.Get("InteractiveStateRejected", "Rejected")
                : CoreStrings.Get("InteractiveStateCompleted", "Completed"),
        };

    [JsonIgnore]
    public string InteractiveStateDescription =>
        InteractivePhase == InteractiveCardPhase.Pending && Status != EventStatus.Running
            ? HistoricalInteractiveStateDescription
            : EffectiveInteractivePhase switch
            {
                InteractiveCardPhase.Pending => CoreStrings.Get(
                    "InteractiveDescPending", "The agent is paused until you respond."),
                InteractiveCardPhase.Submitting
                    => CoreStrings.Get("InteractiveDescSubmitting", "Sending your response…"),
                InteractiveCardPhase.Submitted
                    => CoreStrings.Get("InteractiveDescSubmitted", "Your response was sent."),
                InteractiveCardPhase.Completed
                    => CoreStrings.Get("InteractiveDescCompleted", "This request has been completed."),
                InteractiveCardPhase.Cancelled
                    => CoreStrings.Get("InteractiveDescCancelled", "This request was cancelled."),
                InteractiveCardPhase.Rejected
                    => CoreStrings.Get("InteractiveDescRejected", "The request was rejected."),
                InteractiveCardPhase.Expired
                    => CoreStrings.Get("InteractiveDescExpired", "This request is no longer active."),
                InteractiveCardPhase.ConnectionLost => CoreStrings.Get(
                    "InteractiveDescConnectionLost",
                    "Your input is preserved. Reconnect before responding."),
                InteractiveCardPhase.Error => CoreStrings.Get(
                    "InteractiveDescError", "The response was not sent. Your input is preserved."),
                _ => "",
            };

    /// <summary>See <see cref="HistoricalInteractiveStateLabel"/>: match arms are stored
    /// markers, only the descriptions are localized.
    ///
    /// <para>The fallback arm checks <see cref="Status"/> for the same reason the label's does.
    /// It used to return "completed" unconditionally, so a rejected card whose
    /// <c>event_meta</c> did not literally read <c>"Rejected"</c> showed a <b>Rejected</b> badge
    /// above the line "This request has been completed." — the two halves of one card
    /// disagreeing, from the same row, because only one of them looked at the status.</para></summary>
    private string HistoricalInteractiveStateDescription
        => EventMeta switch
        {
            "Skipped"
                => CoreStrings.Get("InteractiveDescSkipped", "No response was submitted."),
            "Rejected"
                => CoreStrings.Get("InteractiveDescWasRejected", "This request was rejected."),
            { } value when value.StartsWith("Answered:", StringComparison.Ordinal)
                => CoreStrings.Get("InteractiveDescWasSubmitted", "Your response was submitted."),
            _ => Status == EventStatus.Error
                ? CoreStrings.Get("InteractiveDescWasRejected", "This request was rejected.")
                : CoreStrings.Get("InteractiveDescCompleted", "This request has been completed."),
        };

    private InteractiveCardPhase EffectiveInteractivePhase
        => InteractivePhase == InteractiveCardPhase.Pending && Status != EventStatus.Running
            ? Status == EventStatus.Error ? InteractiveCardPhase.Rejected : InteractiveCardPhase.Completed
            : InteractivePhase;

    [JsonIgnore] public int AskUserSelectedCount => AskUserOptionEntries.Count(option => option.IsChecked);
    [JsonIgnore]
    public string AskUserSelectionSummary => AskUserMultiSelect
        ? CoreStrings.Format("AskUserSelectedCount", "{0} selected", AskUserSelectedCount)
        : CoreStrings.Get("AskUserSelectOne", "Select one option");
    [JsonIgnore]
    public string AskUserRequirementText => HasAskUserFields
        ? CoreStrings.Get("AskUserCompleteFields", "Complete the requested fields.")
        : HasAskUserOptions
            ? AskUserMultiSelect
                ? CoreStrings.Get("AskUserChooseMany", "Choose one or more options.")
                : CoreStrings.Get("AskUserChooseOne", "Choose one option.")
            : CoreStrings.Get("AskUserEnterResponse", "Enter your response.");
    [JsonIgnore]
    public bool ShowAskUserSelectionSummary
        => ShowAskUserOptions && AskUserMultiSelect;

    [JsonIgnore]
    public bool CanSubmitAskUser
    {
        get
        {
            if (!IsInteractiveEditable) return false;
            if (AskUserFields.Count > 0)
                return AskUserFields.All(entry => !entry.Required || !string.IsNullOrWhiteSpace(entry.Value));
            return AskUserSelectedCount > 0 || !string.IsNullOrWhiteSpace(AskUserFreeText);
        }
    }

    [JsonIgnore]
    public DiffPreviewModel DiffPreview
        => _diffPreview ??= DiffPreviewModel.Parse(EventTitle, EventDetail,
            string.Equals(EventEyebrow, "CREATE", StringComparison.Ordinal));
    [JsonIgnore] public string DiffFileName => DiffPathFormatter.FileName(EventTitle);
    [JsonIgnore] public string DiffContextPath => DiffPathFormatter.ContextPath(EventTitle);
    [JsonIgnore] public string DiffDisplayPath => DiffPathFormatter.MiddleEllipsis(EventTitle);
    // The card itself folds; an open diff keeps a bounded inner viewport so one large file never
    // disables chat-list virtualization by becoming the height of the whole document.
    [JsonIgnore] public double DiffViewportHeight => 360;
    [JsonIgnore] public bool CanExpandDiff => DiffPreview.Files.Sum(file => file.Lines.Count) > 12;
    [JsonIgnore]
    public bool CanWrapDiff => DiffPreview.Files
        .SelectMany(file => file.Lines)
        .Any(line => line.Content.Length > 100);
    [JsonIgnore]
    public string DiffExpandLabel => IsDiffExpanded
        ? CoreStrings.Get("DiffCollapse", "Collapse")
        : CoreStrings.Get("DiffExpand", "Expand");
    [JsonIgnore]
    public string DiffWrapLabel => IsDiffWrapped
        ? CoreStrings.Get("DiffNoWrap", "No wrap")
        : CoreStrings.Get("DiffWrap", "Wrap");
    [JsonIgnore] public DiffChangeState DiffState => ParseDiffState(EventResult);
    [JsonIgnore] public bool ShowDiffBody => IsDiffExpanded;

    /// <summary>Always. This used to exclude <see cref="DiffChangeState.Pending"/> — i.e. the one
    /// state in which folding actually matters: a long diff awaiting approval pushes the buttons
    /// that answer it off the bottom of the viewport, and hiding the only control that could
    /// shorten it made that unrecoverable without scrolling past the decision.</summary>
    [JsonIgnore] public bool CanToggleDiffVisibility => true;
    [JsonIgnore]
    public bool ShowDiffProblem => DiffState is DiffChangeState.Failed
        or DiffChangeState.PartiallyApplied or DiffChangeState.Disconnected or DiffChangeState.Unconfirmed;
    [JsonIgnore]
    public string DiffCardTitle => DiffState switch
    {
        DiffChangeState.Applied
            => CoreStrings.Format("DiffTitleApplied", "Changes applied to {0}", DiffFileName),
        DiffChangeState.Rejected
            => CoreStrings.Get("DiffTitleRejected", "Proposed changes rejected"),
        DiffChangeState.Failed
            => CoreStrings.Get("DiffTitleFailed", "Changes could not be applied"),
        DiffChangeState.PartiallyApplied
            => CoreStrings.Get("DiffTitlePartial", "Changes may be partially applied"),
        DiffChangeState.Disconnected
            => CoreStrings.Get("DiffTitleDisconnected", "Connection lost while applying changes"),
        DiffChangeState.Unconfirmed
            => CoreStrings.Get("DiffTitleUnconfirmed", "Change result could not be confirmed"),
        DiffChangeState.Applying
            => CoreStrings.Get("DiffTitleApplying", "Applying file changes"),
        DiffChangeState.Preview => CoreStrings.Get("DiffTitleReview", "Review file changes"),
        _ => CoreStrings.Get("DiffTitleReview", "Review file changes"),
    };
    [JsonIgnore]
    public string DiffCardSubtitle => DiffState == DiffChangeState.Applied
        ? ""
        : DiffState == DiffChangeState.Rejected
            ? DiffFileName
            : DiffPreview.FileSummary;
    [JsonIgnore] public string DiffHeaderMeta => DiffPreview.ChangeSummary;
    [JsonIgnore]
    public string DiffIconGlyph => DiffState switch
    {
        DiffChangeState.Applied => "\uE73E",
        DiffChangeState.Rejected or DiffChangeState.Failed => "\uE711",
        DiffChangeState.PartiallyApplied or DiffChangeState.Disconnected or DiffChangeState.Unconfirmed => "\uE7BA",
        _ => "\uE8A5",
    };
    [JsonIgnore]
    public InteractiveVisualTone DiffIconTone => DiffState switch
    {
        DiffChangeState.Applied => InteractiveVisualTone.Success,
        DiffChangeState.Rejected or DiffChangeState.Failed => InteractiveVisualTone.Danger,
        DiffChangeState.Pending or DiffChangeState.Applying or DiffChangeState.PartiallyApplied
            or DiffChangeState.Disconnected or DiffChangeState.Unconfirmed => InteractiveVisualTone.Warning,
        _ => InteractiveVisualTone.Neutral,
    };
    [JsonIgnore]
    public InteractiveVisualTone DiffChromeTone
        => DiffState == DiffChangeState.Pending ? InteractiveVisualTone.Warning : InteractiveVisualTone.Neutral;
    [JsonIgnore]
    public string DiffToggleLabel => IsDiffExpanded
        ? CoreStrings.Get("DiffHideChanges", "Hide changes")
        : DiffState == DiffChangeState.Rejected
            ? CoreStrings.Get("DiffViewProposed", "View proposed changes")
            : CoreStrings.Get("DiffViewChanges", "View changes");
    [JsonIgnore]
    public string DiffProblemText => DiffState switch
    {
        DiffChangeState.Failed => CoreStrings.Get(
            "DiffProblemFailed",
            "The file tool reported an error. Review the proposed changes and tool details."),
        DiffChangeState.PartiallyApplied => CoreStrings.Get(
            "DiffProblemPartial",
            "The tool reported a partial result. Verify the file before continuing."),
        DiffChangeState.Disconnected => CoreStrings.Get(
            "DiffProblemDisconnected",
            "The connection ended before the file result was confirmed."),
        _ => CoreStrings.Get(
            "DiffProblemUnconfirmed",
            "The turn ended without a successful file result. The proposed changes are shown below."),
    };
    [JsonIgnore]
    public string DiffAccessibilityName
        => $"{DiffCardTitle}. {DiffFileName}. {DiffPreview.ChangeSummary}. " +
           $"{(IsDiffExpanded ? CoreStrings.Get("DiffChangesVisible", "Changes visible") : DiffToggleLabel)}.";
    [JsonIgnore] public bool CanSendPlanFeedback => IsInteractiveEditable && !string.IsNullOrWhiteSpace(PlanFeedback);

    public bool ValidateAskUser()
    {
        var valid = true;
        foreach (var field in AskUserFields)
        {
            field.ValidationError = field.Required && string.IsNullOrWhiteSpace(field.Value)
                ? $"{field.Label} is required." : null;
            valid &= field.ValidationError is null;
        }
        if (AskUserFields.Count == 0 && AskUserSelectedCount == 0 && string.IsNullOrWhiteSpace(AskUserFreeText))
        {
            InteractiveErrorText = "Choose an option or enter an answer.";
            valid = false;
        }
        else if (valid)
        {
            InteractiveErrorText = null;
        }
        return valid;
    }

    public bool TryBeginInteractiveSubmit()
    {
        if (!IsInteractiveEditable) return false;
        InteractiveErrorText = null;
        InteractivePhase = InteractiveCardPhase.Submitting;
        return true;
    }

    public void CompleteInteractiveSubmit(string outcome, bool rejected = false)
    {
        IsPlanFeedbackOpen = false;
        IsPlanRejectConfirmOpen = false;
        EventMeta = outcome;
        InteractivePhase = rejected ? InteractiveCardPhase.Rejected : InteractiveCardPhase.Submitted;
        Status = rejected ? EventStatus.Error : EventStatus.Done;
        // Every settled interactive card folds, not just plan_review. Folding is now the one
        // "this is done" gesture in the transcript: the header keeps the outcome on screen and
        // the chevron keeps the original request one click away.
        IsInteractiveCardExpanded = false;
    }

    public void FailInteractiveSubmit(string message, bool connectionLost)
    {
        InteractiveErrorText = message;
        InteractivePhase = connectionLost ? InteractiveCardPhase.ConnectionLost : InteractiveCardPhase.Error;
    }

    public void RevalidateInteractiveRequest()
    {
        if (Status != EventStatus.Running || InteractivePhase != InteractiveCardPhase.ConnectionLost) return;
        InteractiveErrorText = null;
        InteractivePhase = InteractiveCardPhase.Pending;
    }

    public void MarkInteractiveConnectionLost()
    {
        if (Status != EventStatus.Running || InteractivePhase == InteractiveCardPhase.Submitting) return;
        InteractiveErrorText = "Connection lost before this response was submitted.";
        InteractivePhase = InteractiveCardPhase.ConnectionLost;
    }

    public void NotifyAskUserInputChanged()
    {
        InteractiveErrorText = null;
        OnPropertyChanged(nameof(AskUserSelectedCount));
        OnPropertyChanged(nameof(AskUserSelectionSummary));
        OnPropertyChanged(nameof(CanSubmitAskUser));
        OnPropertyChanged(nameof(AskUserSubmitLabel));
    }

    partial void OnAskUserFreeTextChanged(string value) => NotifyAskUserInputChanged();

    partial void OnPlanFeedbackChanged(string value)
        => OnPropertyChanged(nameof(CanSendPlanFeedback));

    partial void OnInteractivePhaseChanged(InteractiveCardPhase value)
    {
        OnPropertyChanged(nameof(AskUserSubmitLabel));
        OnPropertyChanged(nameof(PlanReviewSubmitLabel));
        OnPropertyChanged(nameof(AskUserResultTitle));
        NotifyInteractivePresentationChanged();
    }

    partial void OnEventMetaChanged(string? value)
    {
        if (IsAskUserEvent && RelatedDiffPreview is not null)
            RelatedDiffPreview.ApplyDiffApprovalAnswer(value);
        NotifyInteractivePresentationChanged();
    }
    partial void OnEventTitleChanged(string value)
    {
        _diffPreview = null;
        NotifyInteractivePresentationChanged();
        NotifyDiffPresentationChanged();
    }
    partial void OnEventEyebrowChanged(string value) => _diffPreview = null;
    partial void OnEventKindChanged(string value)
    {
        if (value == "diff_preview") SynchronizeDiffPresentation();
        OnPropertyChanged(nameof(IsTranscriptRowVisible));
    }
    partial void OnEventResultChanged(string? value)
    {
        if (IsDiffPreviewEvent) SynchronizeDiffPresentation();
    }
    partial void OnIsDiffExpandedChanged(bool value) => NotifyDiffPresentationChanged();

    internal void NotifyInteractivePresentationChanged()
    {
        OnPropertyChanged(nameof(ShowAskUserPendingContent));
        OnPropertyChanged(nameof(ShowAskUserBody));
        OnPropertyChanged(nameof(ShowAskUserStatusBadge));
        OnPropertyChanged(nameof(IsAskUserCompact));
        OnPropertyChanged(nameof(AskUserChromeTone));
        OnPropertyChanged(nameof(AskUserIconTone));
        OnPropertyChanged(nameof(AskUserIconGlyph));
        OnPropertyChanged(nameof(IsFileChangeApproval));
        OnPropertyChanged(nameof(AskUserTargetPath));
        OnPropertyChanged(nameof(AskUserTargetFileName));
        OnPropertyChanged(nameof(AskUserContextPath));
        OnPropertyChanged(nameof(AskUserDisplayQuestion));
        OnPropertyChanged(nameof(AskUserCardTitle));
        OnPropertyChanged(nameof(AskUserCardSubtitle));
        OnPropertyChanged(nameof(AskUserSubmitLabel));
        OnPropertyChanged(nameof(ShowAskUserSkipAction));

        // The plan card's chrome reads Status exactly the way the ask_user block above does, and
        // it was missing from this funnel entirely — these four were added when the plan card
        // gained tones (it had none, so every state drew Neutral) but nothing ever raised them.
        // Approving or rejecting a plan changed Status without changing a single colour: the card
        // kept the amber "waiting" rail, the amber clock, and a subtitle still claiming the agent
        // would not proceed until the user responded. It corrected itself only on a container
        // recycle or a reload, which is why it looked intermittent.
        OnPropertyChanged(nameof(PlanChromeTone));
        OnPropertyChanged(nameof(PlanIconTone));
        OnPropertyChanged(nameof(PlanIconGlyph));
        OnPropertyChanged(nameof(PlanReviewSubtitle));

        OnPropertyChanged(nameof(IsTranscriptRowVisible));
        OnPropertyChanged(nameof(CanRecallDiff));
        RelatedDiffPreview?.NotifyRelatedDiffApprovalChanged();
    }

    private bool IsRejectedAskUserResult
        => string.Equals(InteractiveOutcome, "Rejected", StringComparison.OrdinalIgnoreCase)
            || InteractiveAnswerText.StartsWith("No, reject", StringComparison.OrdinalIgnoreCase);

    // InteractiveAnswerText is the answer as it went on the wire and InteractiveOutcome is the
    // stored marker; both stay English on the match side. Only the resolved wording is localized.
    private string AskUserResolvedTitle
        => InteractiveAnswerText switch
        {
            "Yes, apply this change"
                => CoreStrings.Get("AskUserResolvedApproved", "Change approved"),
            "Yes to all (auto-approve)"
                => CoreStrings.Get("AskUserResolvedApprovedAll", "Similar changes approved"),
            { } answer when answer.StartsWith("No, reject", StringComparison.OrdinalIgnoreCase)
                => CoreStrings.Get("AskUserResolvedChangeRejected", "Change rejected"),
            _ when string.Equals(InteractiveOutcome, "Rejected", StringComparison.OrdinalIgnoreCase)
                => CoreStrings.Get("AskUserResolvedRequestRejected", "Request rejected"),
            _ when string.Equals(InteractiveOutcome, "Skipped", StringComparison.OrdinalIgnoreCase)
                => CoreStrings.Get("AskUserResolvedRequestClosed", "Request closed"),
            _ => CoreStrings.Get("AskUserResultSubmitted", "Response submitted"),
        };

    private string AskUserResolvedSubtitle
        => InteractiveAnswerText switch
        {
            "Yes, apply this change" when IsFileChangeApproval => CoreStrings.Format(
                "AskUserResolvedAppliedOnce", "Applied once to {0}", AskUserTargetFileName),
            "Yes to all (auto-approve)" => CoreStrings.Get(
                "AskUserResolvedAutoApproval", "Auto-approval enabled for this session"),
            { } answer when answer.StartsWith("No, reject", StringComparison.OrdinalIgnoreCase)
                => CoreStrings.Get(
                    "AskUserResolvedFeedbackRequested", "Feedback requested from the agent"),
            // The user's own answer, echoed back verbatim — never translated.
            { Length: > 0 } answer => answer,
            _ when string.Equals(InteractiveOutcome, "Skipped", StringComparison.OrdinalIgnoreCase)
                => CoreStrings.Get("AskUserResolvedNoResponse", "No response was submitted"),
            _ => CoreStrings.Get("AskUserResolvedSent", "Response sent to the agent"),
        };

    private bool TryGetAskUserTargetPath(out string path)
    {
        path = "";
        var question = EventTitle.Trim();
        string? prefix = question.StartsWith("Apply changes to ", StringComparison.OrdinalIgnoreCase)
            ? "Apply changes to "
            : question.StartsWith("Apply this change to ", StringComparison.OrdinalIgnoreCase)
                ? "Apply this change to " : null;
        if (prefix is null) return false;
        path = question[prefix.Length..].Trim().TrimEnd('?');
        return path.Length > 0;
    }

    /// <summary>The diff paired with this ask_user card. Transient by design: replay reconstructs
    /// the relation from event order, while the durable outcome lives on the diff itself.</summary>
    [JsonIgnore] public ChatMessage? RelatedDiffPreview { get; private set; }

    /// <summary>The ask_user decision surface rendered directly beneath this diff while pending.</summary>
    [JsonIgnore] public ChatMessage? RelatedDiffApproval { get; private set; }

    [JsonIgnore]
    public bool ShowRelatedDiffApprovalCard
        => RelatedDiffApproval is { Status: EventStatus.Running };

    /// <summary>Whether the paired decision has been made — approved, rejected, or sealed as
    /// unanswered when the run ended. A diff with no approval at all (an informational
    /// <c>remote_diff_preview</c>) is never "settled" by this rule and is never hidden by it.</summary>
    [JsonIgnore]
    public bool HasSettledDiffApproval
        => RelatedDiffApproval is { Status: not EventStatus.Running };

    /// <summary>Messages remain in the projection for answer and persistence alignment even when
    /// another card owns their UI. Collapsing the container as well as selecting an empty template
    /// prevents those implementation-only rows from leaving a six-pixel transcript gap.
    ///
    /// <para><b>One rule governs the diff/decision pair: a decision that is still pending is
    /// hosted by its diff; a decision that has settled always shows its own folded card.</b> While
    /// the agent is blocked, the proposal and the buttons that answer it must not be separable, so
    /// the ask_user row hides and <c>ChatDiffPreviewTemplate</c> renders it beneath the diff. Once
    /// answered, the roles swap: the diff withdraws and the settled card carries the outcome.</para>
    ///
    /// <para>Note the ask_user side deliberately does <i>not</i> ask whether the diff is still
    /// visible. It used to, and that produced the one case where a decision vanished entirely: a
    /// change the user approved but whose write then <b>failed</b> keeps its diff on screen (that
    /// is where <c>DiffProblemText</c> lives), so a visibility-coupled rule kept hiding the
    /// approval — in precisely the situation where "who approved this, and to what" matters
    /// most.</para></summary>
    [JsonIgnore]
    public bool IsTranscriptRowVisible
        => !(IsAskUserEvent && RelatedDiffPreview is not null && Status == EventStatus.Running)
            && !IsWithdrawnDiffPreview;

    /// <summary>A diff that has done its job: its decision is settled and the file reached a
    /// terminal, unsurprising state. It leaves the transcript rather than sitting there as a
    /// several-hundred-pixel panel nobody can act on any more.
    ///
    /// <para>Deliberately <b>not</b> a plain "the approval is over" test. <see
    /// cref="DiffChangeState.Failed"/>, <see cref="DiffChangeState.PartiallyApplied"/>, <see
    /// cref="DiffChangeState.Disconnected"/> and <see cref="DiffChangeState.Unconfirmed"/> all mean
    /// the file may not match what was shown, and the diff body plus <c>DiffProblemText</c> are the
    /// only place that is reported — those stay. So do <see cref="DiffChangeState.Applying"/> (the
    /// write is still in flight) and <see cref="DiffChangeState.Preview"/> (informational, no
    /// decision was ever asked for).</para>
    ///
    /// <para>The row is only hidden, never removed from the projection: the message still persists,
    /// still anchors <c>ResolveInteractiveCards</c>' answer alignment, and — critically — is still
    /// there for <c>ToolActivityMigration.PairRelatedDiffApprovals</c> to find when the
    /// conversation is reloaded. Dropping the row would break that pairing on restore, and the
    /// answered card would reappear as a standalone row it never was while live.</para></summary>
    [JsonIgnore]
    public bool IsWithdrawnDiffPreview
        => IsDiffPreviewEvent
            && HasSettledDiffApproval
            && DiffState is DiffChangeState.Applied or DiffChangeState.Rejected
            && !IsDiffRecalled;

    /// <summary>Set by the settled decision card's "View changes" action to bring a withdrawn diff
    /// back. One-way and per-session: nothing puts it back to false, because the user asking to see
    /// the diff again outranks the tidying rule that took it away.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWithdrawnDiffPreview))]
    [NotifyPropertyChangedFor(nameof(IsTranscriptRowVisible))]
    [JsonIgnore]
    public partial bool IsDiffRecalled { get; set; }

    public void AttachRelatedDiffPreview(ChatMessage? diff)
    {
        if (ReferenceEquals(RelatedDiffPreview, diff)) return;

        if (RelatedDiffPreview is { } previous && ReferenceEquals(previous.RelatedDiffApproval, this))
        {
            previous.RelatedDiffApproval = null;
            previous.NotifyRelatedDiffApprovalChanged();
        }

        RelatedDiffPreview = diff;
        OnPropertyChanged(nameof(RelatedDiffPreview));
        OnPropertyChanged(nameof(IsTranscriptRowVisible));

        if (diff is null) return;
        diff.RelatedDiffApproval = this;
        diff.OnPropertyChanged(nameof(RelatedDiffApproval));
        diff.NotifyRelatedDiffApprovalChanged();
        diff.ApplyDiffApprovalAnswer(EventMeta);
    }

    /// <summary>Raised on the <i>diff</i> when its paired decision moves. The diff's own row
    /// visibility is a function of the approval's status, so it cannot be notified from the
    /// diff's setters alone.</summary>
    internal void NotifyRelatedDiffApprovalChanged()
    {
        OnPropertyChanged(nameof(ShowRelatedDiffApprovalCard));
        OnPropertyChanged(nameof(HasSettledDiffApproval));
        OnPropertyChanged(nameof(IsWithdrawnDiffPreview));
        OnPropertyChanged(nameof(IsTranscriptRowVisible));
        OnPropertyChanged(nameof(CanRecallDiff));
    }

    /// <summary>Whether the settled decision card should offer a way back to the diff it hid.
    /// Only meaningful on the ask_user side, and only while the diff is actually withdrawn —
    /// once recalled, the diff is on screen and the offer would point at nothing.</summary>
    [JsonIgnore]
    public bool CanRecallDiff
        => IsAskUserEvent && RelatedDiffPreview is { IsWithdrawnDiffPreview: true };

    /// <summary>Brings a withdrawn diff back onto the transcript, expanded. Invoked from the
    /// settled decision card, which is the only surface still on screen that knows the diff
    /// exists.</summary>
    public void RecallRelatedDiff()
    {
        if (RelatedDiffPreview is not { } diff) return;
        diff.IsDiffRecalled = true;
        diff._diffExpansionOverridden = true;
        diff.IsDiffExpanded = true;
        OnPropertyChanged(nameof(CanRecallDiff));
    }

    public void ToggleDiffExpanded()
    {
        if (!CanToggleDiffVisibility) return;
        _diffExpansionOverridden = true;
        IsDiffExpanded = !IsDiffExpanded;
    }

    public void SetDiffState(DiffChangeState state)
    {
        if (!IsDiffPreviewEvent) return;
        EventResult = DiffStatePrefix + state;
    }

    public void ApplyDiffApprovalAnswer(string? outcome)
    {
        if (!IsDiffPreviewEvent || string.IsNullOrWhiteSpace(outcome)) return;
        var answer = outcome.StartsWith("Answered: ", StringComparison.Ordinal)
            ? outcome[10..] : outcome;
        if (answer.StartsWith("No, reject", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "Skipped", StringComparison.OrdinalIgnoreCase))
        {
            SetDiffState(DiffChangeState.Rejected);
            return;
        }
        if ((answer == "Yes, apply this change" || answer == "Yes to all (auto-approve)")
            && DiffState is DiffChangeState.Pending or DiffChangeState.Preview)
        {
            SetDiffState(DiffChangeState.Applying);
        }
    }

    private static DiffChangeState ParseDiffState(string? value)
        => value is not null && value.StartsWith(DiffStatePrefix, StringComparison.Ordinal)
            && Enum.TryParse<DiffChangeState>(value[DiffStatePrefix.Length..], out var state)
                ? state
                : DiffChangeState.Unconfirmed;

    /// <summary>Moves the card's disclosure to the default for a newly reached state.
    ///
    /// <para><see cref="DiffChangeState.Applying"/> collapses along with the two terminal states.
    /// It is a transient "the write is in flight" marker with nothing to decide and nothing yet to
    /// inspect, and leaving it expanded is what produced the worst moment in this flow: the instant
    /// an approval was answered its card unloaded, and the several-hundred-pixel diff it had been
    /// attached to stayed open, alone, until a <c>tool_result</c> arrived — or until the end of the
    /// turn if one never did.</para>
    ///
    /// <para>Failed/PartiallyApplied/Disconnected/Unconfirmed stay expanded on purpose: the body
    /// and <see cref="DiffProblemText"/> are the only report that the file may not match what was
    /// shown.</para>
    ///
    /// <para>Gated on <see cref="_diffExpansionOverridden"/> so it stops steering once the user has
    /// said what they want for this card — same contract as
    /// <c>ToolActivityViewModel.HasUserExpansionOverride</c>, and the reason it matters here is
    /// that a diff moves through up to three states in one turn, so an ungated rule overrides the
    /// user's choice repeatedly rather than once.</para></summary>
    private void SynchronizeDiffPresentation()
    {
        var state = DiffState;
        if (_presentedDiffState == state) return;
        _presentedDiffState = state;
        if (!_diffExpansionOverridden)
        {
            IsDiffExpanded = state is not (DiffChangeState.Applied
                or DiffChangeState.Rejected
                or DiffChangeState.Applying);
        }
        NotifyDiffPresentationChanged();
    }

    private void NotifyDiffPresentationChanged()
    {
        OnPropertyChanged(nameof(DiffState));
        OnPropertyChanged(nameof(ShowDiffBody));
        OnPropertyChanged(nameof(CanToggleDiffVisibility));
        OnPropertyChanged(nameof(ShowDiffProblem));
        OnPropertyChanged(nameof(DiffCardTitle));
        OnPropertyChanged(nameof(DiffCardSubtitle));
        OnPropertyChanged(nameof(DiffHeaderMeta));
        OnPropertyChanged(nameof(DiffIconGlyph));
        OnPropertyChanged(nameof(DiffIconTone));
        OnPropertyChanged(nameof(DiffChromeTone));
        OnPropertyChanged(nameof(DiffToggleLabel));
        OnPropertyChanged(nameof(DiffProblemText));
        OnPropertyChanged(nameof(DiffAccessibilityName));
        // The withdrawal rule reads DiffState, so a state transition can retire the row.
        OnPropertyChanged(nameof(IsWithdrawnDiffPreview));
        OnPropertyChanged(nameof(IsTranscriptRowVisible));
        RelatedDiffApproval?.OnPropertyChanged(nameof(CanRecallDiff));
    }
    public void ToggleDiffWrap()
    {
        IsDiffWrapped = !IsDiffWrapped;
        DiffPreview.IsWrapped = IsDiffWrapped;
        foreach (var file in DiffPreview.Files)
            foreach (var line in file.Lines)
                line.IsWrapped = IsDiffWrapped;
    }
    public void OpenPlanFeedback() => IsPlanFeedbackOpen = true;
    public void ClosePlanFeedback() => IsPlanFeedbackOpen = false;
    public void OpenPlanRejectConfirmation() => IsPlanRejectConfirmOpen = true;
    public void ClosePlanRejectConfirmation() => IsPlanRejectConfirmOpen = false;
}
