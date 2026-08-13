using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ConnectOnion.WinUIClient.Models;

/// <summary>Controls how an assistant turn's tool activity is rendered.</summary>
public enum ToolDisplayMode { Hidden, Compact, Detailed }

public enum ToolActivityStatus
{
    Running,
    Success,
    PartialSuccess,
    Failed,
    Cancelled,
    WaitingForConfirmation,
    WaitingForPermission,
}

public enum ToolStepStatus { Pending, Running, Success, Warning, Failed, Cancelled }

/// <summary>
/// Durable, UI-facing aggregate for every tool invocation in one assistant turn. It is
/// deliberately independent of a page: the stream projector mutates it and the existing
/// message repository serializes it with the chat message, so re-entering a session shows
/// exactly the same compact timeline.
/// </summary>
public sealed partial class ToolActivityViewModel : Common.ObservableObject
{
    public ToolActivityViewModel()
    {
        Status = ToolActivityStatus.Running;
        DisplayMode = ToolDisplayMode.Compact;
        // Starts collapsed; ToolActivityProjector.ApplyAutoExpansion opens it as soon as a tool
        // actually starts on a live view and folds it again when the run finishes. The seed
        // matters for the headless paths (persistence, migration) that never call the projector's
        // live rules — those must write the card closed, so reopening an old conversation does
        // not replay every past run at full height.
        IsExpanded = false;
        Summary = "Running tools…";
        StartedAt = DateTimeOffset.UtcNow;
        _steps.CollectionChanged += OnStepsChanged;
    }

    public string TurnId { get; set; } = Guid.NewGuid().ToString("N");

    private ObservableCollection<ToolStepViewModel> _steps = new();

    /// <summary>
    /// The turn's tool invocations, in order.
    ///
    /// <para>Hand-written rather than an auto-property because the card's <i>shape</i> now depends
    /// on how many steps there are (see <see cref="IsSingleStep"/>), and a plain collection raises
    /// nothing on the activity when an item is added. Both the initial collection and any
    /// replacement — deserialization assigns a fresh one — are subscribed here, so the count-derived
    /// properties stay live wherever the steps came from.</para>
    /// </summary>
    public ObservableCollection<ToolStepViewModel> Steps
    {
        get => _steps;
        set
        {
            if (ReferenceEquals(_steps, value)) return;
            _steps.CollectionChanged -= OnStepsChanged;
            _steps = value ?? new();
            _steps.CollectionChanged += OnStepsChanged;
            OnPresentationChanged();
        }
    }

    private void OnStepsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPresentationChanged();

    [ObservableProperty]
    public partial ToolActivityStatus Status { get; set; }

    partial void OnStatusChanged(ToolActivityStatus value) => OnPresentationChanged();

    /// <summary>
    /// The approval request that blocks this turn, rendered <b>inside</b> this card below the
    /// timeline (see <c>ToolActivityView.xaml</c>) so the tool activity and the decision read as one
    /// card. Set by the projection when an <c>approval_needed</c> frame arrives; <c>[JsonIgnore]</c>
    /// because approvals are live-only — a reloaded conversation shows the timeline outcome, not a
    /// dead decision (the persist path drops the approval entirely).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApproval))]
    [JsonIgnore]
    public partial ChatMessage? Approval { get; set; }

    // Embedding an approval turns the card's chrome/timeline on, and the card's "waiting" display
    // follows the *approval's own* pending state (not this card's ToolActivityStatus), so subscribe
    // to it and re-present when it resolves. Two-param hook so the previous approval is unsubscribed
    // (no dangling handler when a turn rolls over to a fresh card for its next approval).
    partial void OnApprovalChanged(ChatMessage? oldValue, ChatMessage? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnApprovalMessagePropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnApprovalMessagePropertyChanged;
        SyncApprovalSupersededStep();
        OnPresentationChanged();
    }

    private void OnApprovalMessagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessage.IsApprovalPending))
        {
            SyncApprovalSupersededStep();
            OnPropertyChanged(nameof(IsAwaitingApproval));
            OnPropertyChanged(nameof(ShowCompletionMarker));
            OnPropertyChanged(nameof(ShowCollapsedStepCount));
        }
    }

    /// <summary>Silences the step row's one-line command preview while the approval below is
    /// showing the same command in full.
    ///
    /// <para>The two are the same string: the step's invocation is read from the tool call's
    /// arguments and the approval's command block from the approval request's, for the same call.
    /// So a pending approval put the command on screen twice, a few pixels apart — once trimmed to
    /// a single line and once complete with a copy button — which reads as two different things
    /// having been proposed.</para>
    ///
    /// <para>Matched on the tool's display name rather than on the command text: the approval's
    /// <c>EventTitle</c> is <c>ToolActivityProjector.DisplayName(toolName)</c>, exactly what the
    /// step's <see cref="ToolStepViewModel.DisplayName"/> holds, while the two command strings are
    /// formatted by different code paths and are not reliably identical. Only a non-terminal step
    /// qualifies — a finished one is history and its preview is the record of what ran.</para></summary>
    private void SyncApprovalSupersededStep()
    {
        var pendingTool = Approval is { IsApprovalPending: true } approval ? approval.EventTitle : null;
        foreach (var step in Steps)
        {
            step.IsSupersededByApproval = pendingTool is not null
                && step.Status is ToolStepStatus.Running or ToolStepStatus.Pending
                && string.Equals(step.DisplayName, pendingTool, StringComparison.Ordinal);
        }
    }

    [JsonIgnore] public bool HasApproval => Approval is not null;

    /// <summary>"7 steps" on the header while an approval is pending and the timeline is folded.
    ///
    /// <para>The header's <see cref="Summary"/> slot is deliberately blank whenever an approval is
    /// embedded (the approval carries the state instead), and folding the timeline for the decision
    /// takes the steps off screen too — so without this the card gives no sign that the turn did
    /// anything before it stopped. It reuses that empty slot rather than adding a row.</para></summary>
    [JsonIgnore]
    public string CollapsedStepCountHint => Steps.Count switch
    {
        0 => "",
        1 => Common.CoreStrings.Get("ToolActivityOneStep", "1 step"),
        var count => Common.CoreStrings.Format("ToolActivityStepCount", "{0} steps", count),
    };

    [JsonIgnore]
    public bool ShowCollapsedStepCount
        => IsAwaitingApproval && !IsExpanded && Steps.Count > 0;
    [JsonIgnore]
    public string HeaderTitle => HasApproval && !string.IsNullOrWhiteSpace(Approval!.EventTitle)
        ? Approval.EventTitle
        : Common.CoreStrings.Get("ToolActivityHeader", "Tool activity");
    [JsonIgnore]
    public ToolIconKind HeaderIconKind => HasApproval
        ? ToolIcons.ForTool(Approval!.EventTitle)
        : ToolIconKind.Generic;

    /// <summary>The card is parked on a decision. Driven by the <b>live</b> approval still being
    /// pending — never by this card's <see cref="Status"/>, which lags (it stays
    /// WaitingForConfirmation until the turn's Complete runs) and is absent on reload (the approval
    /// is <c>[JsonIgnore]</c>). So a card loaded from history, or one whose approval was just
    /// answered, never shows the amber "waiting" chrome. Drives the header's amber status and
    /// suppresses the timeline's completion marker so "waiting" is stated once, not three times.</summary>
    [JsonIgnore]
    public bool IsAwaitingApproval => Approval is { IsApprovalPending: true };

    /// <summary>The timeline's own completion row ("Done"/"Waiting for approval"). Suppressed while
    /// the card is parked on a decision: the embedded approval section is the one place that state
    /// is shown, so the marker would be a second copy of it.</summary>
    [JsonIgnore] public bool ShowCompletionMarker => ShowCardChrome && !IsAwaitingApproval;

    [ObservableProperty]
    public partial ToolDisplayMode DisplayMode { get; set; }

    partial void OnDisplayModeChanged(ToolDisplayMode value)
    {
        if (value == ToolDisplayMode.Detailed) IsExpanded = true;
        OnPresentationChanged();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimelineVisible))]
    [NotifyPropertyChangedFor(nameof(AccessibilityName))]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedError))]
    [NotifyPropertyChangedFor(nameof(ChevronAngle))]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedStepCount))]
    public partial bool IsExpanded { get; set; }

    /// <summary>
    /// Rotation for the header's disclosure chevron, in degrees clockwise: 0 points it down when
    /// open, 270 turns it to point right when closed.
    ///
    /// <para>One rotated <c>ChevronDown</c> rather than swapping a Up/Down pair, matching the
    /// sidebar's agent rows (<c>ShellAgentItem.ChevronAngle</c>) — the same control in the same
    /// app should turn the same way, and down/right is the disclosure convention the sidebar
    /// already set. A plain double, so this stays WinUI-free; the view wraps it in a
    /// RotateTransform.</para>
    /// </summary>
    [JsonIgnore] public double ChevronAngle => IsExpanded ? 0 : 270;

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset StartedAt { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? CompletedAt { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastErrorMessage))]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedError))]
    public partial string? ErrorSummary { get; set; }

    /// <summary>
    /// What actually went wrong, for the one line the collapsed card shows under its header. The
    /// last failed step wins over <see cref="ErrorSummary"/> because that field is only set when
    /// a tool is blocked or the whole turn dies — a single failed step among several leaves it
    /// null, and the header then says nothing more useful than "1 steps had issues".
    ///
    /// First line only: a tool error is routinely a multi-line traceback, and the rest of it is
    /// one click away in the step's own Error block.
    /// </summary>
    [JsonIgnore]
    public string? LastErrorMessage
    {
        get
        {
            for (var i = Steps.Count - 1; i >= 0; i--)
            {
                if (Steps[i].Status != ToolStepStatus.Failed) continue;
                if (FirstLine(Steps[i].Error ?? Steps[i].Summary) is { } stepText) return stepText;
            }
            return FirstLine(ErrorSummary);
        }
    }

    /// <summary>The error line belongs to the collapsed card only. Once open, the failed step
    /// shows the same text in full in its own Error block, and printing it twice made the top of
    /// an expanded card read as two different failures.</summary>
    [JsonIgnore]
    /// <summary>Never shown for a single step: that step is already on screen, un-collapsed, with
    /// the same message in its own Error block — the line exists to rescue a *folded* card.</summary>
    public bool ShowCollapsedError => !IsExpanded && !IsSingleStep && LastErrorMessage is not null;

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.AsSpan().Trim();
        var end = line.IndexOfAny('\r', '\n');
        return (end < 0 ? line : line[..end]).Trim().ToString() is { Length: > 0 } result ? result : null;
    }

    [JsonIgnore] public bool IsTerminal => Status is ToolActivityStatus.Success or ToolActivityStatus.PartialSuccess or ToolActivityStatus.Failed or ToolActivityStatus.Cancelled;
    [JsonIgnore] public bool IsVisible => DisplayMode != ToolDisplayMode.Hidden || !IsTerminal || Status is ToolActivityStatus.Failed or ToolActivityStatus.PartialSuccess or ToolActivityStatus.WaitingForConfirmation or ToolActivityStatus.WaitingForPermission || Steps.Any(step => step.IsHighRisk);
    /// <summary>
    /// Exactly one tool ran this turn, so the card drops its wrapper and shows that step on its
    /// own — no "Tool activity" header, no completion row, no disclosure. A header summarising a
    /// single row said less than the row ("Tool execution completed · 1 steps"), cost a click to
    /// get past, and made the commonest turn shape the noisiest.
    ///
    /// <para>Count-derived, so it flips the moment a second tool starts and the card grows its
    /// chrome back mid-turn — which is why <see cref="Steps"/> is subscribed rather than a plain
    /// auto-property. Zero steps keeps the chrome: the card exists but has nothing to show yet,
    /// and the header is the only thing saying so.</para>
    /// </summary>
    [JsonIgnore] public bool IsSingleStep => Steps.Count == 1;

    /// <summary>The wrapper: header, collapsed error line and completion row. Present whenever the
    /// card is summarising more than one thing — and always while an approval is embedded, so the
    /// unified card keeps its "Tool activity · Waiting for approval" header even over a single
    /// tool.</summary>
    [JsonIgnore] public bool ShowCardChrome => !IsSingleStep || HasApproval;
    /// <summary>Single-step tool records use the reading measure; multi-step and approval
    /// workflows retain the wider operational track.</summary>
    [JsonIgnore] public double PresentationMaxWidth => ShowCardChrome ? 920 : 760;

    // Only IsExpanded gates the timeline. Status and DisplayMode choose the *default* expansion
    // (see ToolActivityProjector), never override it — ORing "Failed" in here left the header
    // toggle dead for exactly the runs a user most wants to collapse.
    //
    // A single step is forced visible only when it has no disclosure header. Embedding an approval
    // restores the header even for one step; in that shape the user's collapse choice must remove
    // the timeline from layout. Keeping the old `|| IsSingleStep` rule left the panel measured but
    // transparent after the disclosure animation, producing a large blank region above the
    // resolved approval summary.
    [JsonIgnore] public bool IsTimelineVisible => IsVisible && (IsExpanded || !ShowCardChrome);
    [JsonIgnore] public bool IsDetailed => DisplayMode == ToolDisplayMode.Detailed;
    [JsonIgnore]
    public string StatusGlyph => Status switch
    {
        ToolActivityStatus.Success => "✓",
        ToolActivityStatus.PartialSuccess => "⚠",
        ToolActivityStatus.Failed => "✕",
        ToolActivityStatus.Cancelled => "⊘",
        ToolActivityStatus.WaitingForConfirmation => "?",
        ToolActivityStatus.WaitingForPermission => "⚿",
        _ => "◌",
    };
    [JsonIgnore]
    public string CompletionLabel => Status switch
    {
        ToolActivityStatus.Success => Common.CoreStrings.Get("ToolStatusDone", "Done"),
        ToolActivityStatus.PartialSuccess
            => Common.CoreStrings.Get("ToolStatusWarnings", "Completed with warnings"),
        ToolActivityStatus.Failed => Common.CoreStrings.Get("ToolStatusFailed", "Failed"),
        ToolActivityStatus.Cancelled
            => Common.CoreStrings.Get("ToolStatusCancelled", "Cancelled"),
        ToolActivityStatus.WaitingForConfirmation
            => Common.CoreStrings.Get("ToolStatusWaitingApproval", "Waiting for approval"),
        ToolActivityStatus.WaitingForPermission
            => Common.CoreStrings.Get("ToolStatusWaitingApproval", "Waiting for approval"),
        _ => Common.CoreStrings.Get("ToolStatusWorking", "Working..."),
    };
    [JsonIgnore]
    public string AccessibilityName
        => $"{Common.CoreStrings.Get("ToolActivityHeader", "Tool activity")}. {Summary}. " +
           $"{CompletionLabel}. " +
           $"{(IsExpanded ? Common.CoreStrings.Get("DiffCollapse", "Collapse") : Common.CoreStrings.Get("DiffExpand", "Expand"))}";
    [JsonIgnore] public double DurationSeconds => Math.Max(0, ((CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt).TotalSeconds);
    [JsonIgnore] public string DurationLabel => DurationSeconds < 1 ? $"{DurationSeconds * 1000:0} ms" : $"{DurationSeconds:0.0} s";

    /// <summary>
    /// True once the user has opened or closed this card by hand. Auto-expansion stops applying
    /// from that moment for the life of the card.
    /// <para>This is what makes "expand while running" safe. Without it, a user who folds a noisy
    /// card mid-turn has it thrown back open by the very next <c>tool_call</c> — the card fights
    /// them once per tool, which is exactly why auto-expanding on call was removed before.
    /// Not persisted: it is a property of this viewing session, not of the turn.</para>
    /// </summary>
    [JsonIgnore]
    public bool HasUserExpansionOverride { get; private set; }

    public void ToggleExpanded()
    {
        HasUserExpansionOverride = true;
        IsExpanded = !IsExpanded;
    }

    public void RefreshPresentation() => OnPresentationChanged();

    private void OnPresentationChanged()
    {
        OnPropertyChanged(nameof(IsTerminal));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsSingleStep));
        OnPropertyChanged(nameof(ShowCardChrome));
        OnPropertyChanged(nameof(PresentationMaxWidth));
        OnPropertyChanged(nameof(IsTimelineVisible));
        OnPropertyChanged(nameof(IsDetailed));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(CompletionLabel));
        OnPropertyChanged(nameof(AccessibilityName));
        OnPropertyChanged(nameof(DurationLabel));
        // Both read through Steps, which raises nothing on the activity when a step's own status
        // flips. ApplyResult calls RefreshPresentation right after settling a step, so this is
        // where a newly failed step reaches the collapsed error line.
        OnPropertyChanged(nameof(LastErrorMessage));
        OnPropertyChanged(nameof(ShowCollapsedError));
        OnPropertyChanged(nameof(IsAwaitingApproval));
        OnPropertyChanged(nameof(ShowCompletionMarker));
        OnPropertyChanged(nameof(CollapsedStepCountHint));
        OnPropertyChanged(nameof(ShowCollapsedStepCount));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderIconKind));
    }
}

public sealed partial class ToolStepViewModel : Common.ObservableObject
{
    public ToolStepViewModel()
    {
        Status = ToolStepStatus.Running;
        DisplayName = "Tool";
        StartedAt = DateTimeOffset.UtcNow;
    }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Sequence { get; set; }
    public string ToolName { get; set; } = "tool";
    public bool IsHighRisk { get; set; }

    [ObservableProperty]
    public partial ToolStepStatus Status { get; set; }

    partial void OnStatusChanged(ToolStepStatus value) => OnPresentationChanged();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    public partial string DisplayName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisplayTarget))]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    public partial string? DisplayTarget { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    [NotifyPropertyChangedFor(nameof(AccessibilityName))]
    public partial string? Summary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Invocation))]
    [NotifyPropertyChangedFor(nameof(HasInvocation))]
    [NotifyPropertyChangedFor(nameof(ShowInlineInvocation))]
    [NotifyPropertyChangedFor(nameof(InvocationLabel))]
    [NotifyPropertyChangedFor(nameof(InvocationText))]
    [NotifyPropertyChangedFor(nameof(InvocationPrefix))]
    [NotifyPropertyChangedFor(nameof(InvocationDisplayText))]
    [NotifyPropertyChangedFor(nameof(InvocationSecondary))]
    [NotifyPropertyChangedFor(nameof(HasInvocationSecondary))]
    [NotifyPropertyChangedFor(nameof(CanOpenInvocation))]
    // The row title drops its target digest once an invocation carries the real value.
    [NotifyPropertyChangedFor(nameof(ShowDisplayTarget))]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    public partial string? Arguments { get; set; }

    partial void OnArgumentsChanged(string? value) => _invocation = null;

    // ---- What the tool was asked to do ----
    //
    // Derived from the persisted Arguments rather than stored, for the same reason IconKind is
    // derived from ToolName: it costs no column, needs no migration, and conversations recorded
    // before invocations were rendered show their commands as soon as they are reopened.
    //
    // Memoized because the getter parses JSON and the row binds several of these; the cache is
    // dropped whenever Arguments changes (see OnArgumentsChanged), which in practice is once, at
    // projection time.

    private ToolInvocation? _invocation;

    /// <summary>The shell command, search pattern, URL or task this step was given. See
    /// <see cref="ToolInvocations"/>.</summary>
    [JsonIgnore] public ToolInvocation Invocation => _invocation ??= ToolInvocations.Read(ToolName, Arguments);

    /// <summary>Whether there is an invocation worth a line. False leaves the row exactly as it
    /// looked before — a tool taking no readable arguments must not gain an empty block.</summary>
    [JsonIgnore] public bool HasInvocation => Invocation.HasValue;
    [JsonIgnore]
    public bool CanOpenInvocation
        => Invocation.Kind == ToolInvocationKind.Url
            && Uri.TryCreate(Invocation.Text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>The trimmed one-liner under the step's name, shown only while the step is folded.
    /// Once it is open the labelled block below carries the same value in full, and printing both
    /// left the command on screen twice — the same redundancy the single Result/Error block exists
    /// to avoid.</summary>
    [JsonIgnore]
    public bool ShowInlineInvocation
        => HasInvocation && !IsExpanded && !IsSupersededByApproval;

    /// <summary>Set by <c>ToolActivityViewModel.SyncApprovalSupersededStep</c> while a pending
    /// approval below this row is displaying the same command in full. Presentation only, never
    /// persisted — an approval is live-only state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInlineInvocation))]
    [JsonIgnore]
    public partial bool IsSupersededByApproval { get; set; }
    [JsonIgnore] public string InvocationLabel => Invocation.Label;
    /// <summary>The collapsed row's one-line preview. Flattened, not raw — see
    /// <see cref="ToolInvocation.SingleLineText"/>.</summary>
    [JsonIgnore] public string InvocationText => Invocation.SingleLineText;
    /// <summary>Rendered before the text: a shell <c>$</c> for a command, empty otherwise. The gap
    /// after it is layout (the row panel's spacing), never a trailing space — XAML collapses
    /// whitespace between adjacent inlines, which rendered the command as <c>$rm</c>.</summary>
    [JsonIgnore] public string InvocationPrefix => Invocation.Prefix;
    /// <summary>Prefix and text as one string, for the wrapping block that cannot split them.</summary>
    [JsonIgnore] public string InvocationDisplayText => Invocation.DisplayText;
    [JsonIgnore] public string? InvocationSecondary => Invocation.Secondary;
    [JsonIgnore] public bool HasInvocationSecondary => Invocation.HasSecondary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyPropertyChangedFor(nameof(DetailLabel))]
    // Which of the two output blocks renders depends on both fields: an error forces the log.
    [NotifyPropertyChangedFor(nameof(RendersDetailAsMarkdown))]
    [NotifyPropertyChangedFor(nameof(RendersDetailAsLog))]
    public partial string? Result { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyPropertyChangedFor(nameof(DetailLabel))]
    // Which of the two output blocks renders depends on both fields: an error forces the log.
    [NotifyPropertyChangedFor(nameof(RendersDetailAsMarkdown))]
    [NotifyPropertyChangedFor(nameof(RendersDetailAsLog))]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset StartedAt { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? CompletedAt { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationLabel))]
    public partial double? DurationMs { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInlineInvocation))]
    public partial bool IsExpanded { get; set; }

    // ---- Per-step expanded-log UI state ----
    // Purely presentational and transient state for this step's expanded log; never persisted, and
    // it lives for the card's lifetime. Copy needs the clipboard (a UI concern) so it stays in the
    // view's code-behind; the size toggle is pure state and runs through a command here.

    /// <summary>Whether the log region is enlarged (taller) or at its default cap.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogMaxHeight))]
    [NotifyPropertyChangedFor(nameof(EnlargeLabel))]
    [NotifyPropertyChangedFor(nameof(ShowEnlargeButton))]
    public partial bool IsLogEnlarged { get; set; }

    /// <summary>Whether the rendered log is taller than the default cap, so enlarging it actually
    /// reveals more. Set from the log's measured height by the view (a layout fact the model can't
    /// know on its own); a short log that already fits gets no Expand button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEnlargeButton))]
    public partial bool CanEnlargeLog { get; set; }

    /// <summary>Show the Expand/Collapse button only when it does something: the log overflows the
    /// default cap, or it is already enlarged (so the user can still collapse it back).</summary>
    [JsonIgnore] public bool ShowEnlargeButton => CanEnlargeLog || IsLogEnlarged;

    /// <summary>Briefly true after Copy, so the button can show "Copied" without a dialog. The
    /// view sets it and clears it on a short delay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyLabel))]
    [NotifyPropertyChangedFor(nameof(CopyInvocationLabel))]
    public partial bool JustCopied { get; set; }

    /// <summary>Default height cap for the log; ~280px keeps a long result from pushing the final
    /// reply off the page, and Expand roughly doubles it. A fixed enlarged value rather than a
    /// viewport fraction because a DataTemplate has no cheap handle on the page height. Must stay in
    /// step with <c>ToolActivityView.DefaultLogMaxHeight</c>, which decides when Expand even appears.</summary>
    [JsonIgnore] public double LogMaxHeight => IsLogEnlarged ? 560 : 280;
    [JsonIgnore] public string CopyLabel => JustCopied ? "Copied" : "Copy";
    [JsonIgnore] public string CopyInvocationLabel => JustCopied ? "Copied" : "Copy";
    [JsonIgnore] public string EnlargeLabel => IsLogEnlarged ? "Collapse" : "Expand";

    [RelayCommand] private void ToggleLogSize() => IsLogEnlarged = !IsLogEnlarged;

    [JsonIgnore] public string StatusGlyph => Status switch { ToolStepStatus.Success => "✓", ToolStepStatus.Warning => "⚠", ToolStepStatus.Failed => "✕", ToolStepStatus.Cancelled => "⊘", ToolStepStatus.Pending => "·", _ => "◌" };
    /// <summary>Which glyph identifies the tool this step ran. Derived from <see cref="ToolName"/>
    /// rather than stored, so it costs no column, needs no migration, and conversations persisted
    /// before icons existed draw them the moment they are reopened.</summary>
    [JsonIgnore] public ToolIconKind IconKind => ToolIcons.ForTool(ToolName);
    [JsonIgnore] public bool HasDisplayTarget => !string.IsNullOrWhiteSpace(DisplayTarget);

    /// <summary>
    /// The target is a <i>digest</i> of the arguments — a URL's host, a query's first words — so it
    /// stands down once the invocation line below prints the real thing. Otherwise a grep row read
    /// "Grep · Search: TODO" over a line that said "TODO", and a navigation named its host twice.
    /// Kept when there is no invocation: then the digest is all the row has.
    /// </summary>
    [JsonIgnore] public bool ShowDisplayTarget => HasDisplayTarget && !HasInvocation;
    [JsonIgnore] public string DisplayLabel => ShowDisplayTarget ? $"{DisplayName} · {DisplayTarget}" : DisplayName;
    /// <summary>Still true (and still fed to <see cref="AccessibilityName"/>) even though the
    /// collapsed row no longer prints the summary — the result digest reads usefully to a screen
    /// reader, which has no pointer to expand the step with.</summary>
    [JsonIgnore] public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    /// <summary>Currently bound by nothing: the step detail no longer renders an Input block
    /// (see <c>ToolActivityView.xaml</c>). Kept because <see cref="Arguments"/> is still
    /// captured and persisted, so restoring that block is a XAML-only change.</summary>
    [JsonIgnore] public bool HasArguments => !string.IsNullOrWhiteSpace(Arguments);
    [JsonIgnore] public bool HasResult => !string.IsNullOrWhiteSpace(Result);
    [JsonIgnore] public bool HasError => !string.IsNullOrWhiteSpace(Error);

    // ---- The step's one output block ----
    //
    // Result and Error are not two different things to show. On a failed step the projector
    // writes the *same* string into both (ToolActivityProjector.ApplyResult), so rendering a
    // "Result" card and an "Error" card meant printing one message twice in two styles. The UI
    // therefore renders exactly one block, and only its styling changes with Status.
    //
    // Both fields stay on the model and in storage: Error is what marks a step as failed for
    // anything reading the data rather than looking at it, and collapsing them here would lose
    // that distinction.

    /// <summary>The single body to render. Prefers <see cref="Error"/> because on a failure it
    /// is the authoritative text; falls back to <see cref="Result"/> for a normal step.</summary>
    [JsonIgnore] public string? DetailText => HasError ? Error : Result;

    [JsonIgnore] public bool HasDetail => !string.IsNullOrWhiteSpace(DetailText);

    /// <summary>
    /// Render this step's output as markdown prose rather than as a monospace log.
    ///
    /// <para>True only for tools that return documentation (<c>load_guide</c> and friends), and
    /// never for a failed step: an error is a diagnostic whose exact characters matter, and running
    /// a traceback through a markdown renderer mangles it — underscores turn into emphasis, and a
    /// leading <c>#</c> becomes a heading.</para>
    /// </summary>
    [JsonIgnore] public bool RendersDetailAsMarkdown => HasDetail && !HasError && ToolInvocations.ProducesMarkdown(ToolName);

    /// <summary>The monospace log block, which is everything the markdown block is not.</summary>
    [JsonIgnore] public bool RendersDetailAsLog => HasDetail && !RendersDetailAsMarkdown;

    /// <summary>Heading over the block. The accent colour beside it comes from
    /// <see cref="Status"/> via <c>ToolStatusToBrushConverter</c>, so a failed step reads as an
    /// error from its chrome and this label without needing a second card.</summary>
    [JsonIgnore] public string DetailLabel => HasError ? "Error" : "Result";

    /// <summary>The step's own run time, kept deliberately quiet (see the view's low-opacity,
    /// hover-to-reveal treatment). A missing measurement and a genuine zero both render as nothing
    /// — a column of "0 ms" was pure horizontal-scan noise for tools that report no timing — and a
    /// sub-millisecond call reads as "&lt;1 ms" rather than rounding down into that same blank.</summary>
    [JsonIgnore]
    public string DurationLabel => DurationMs switch
    {
        null or <= 0 => "",
        < 1 => "<1 ms",
        < 1000 => $"{DurationMs:0} ms",
        _ => $"{DurationMs / 1000:0.0} s",
    };
    [JsonIgnore] public string AccessibilityName => $"{StatusGlyph} {DisplayName}{(HasSummary ? ", " + Summary : "")}";

    public void ToggleExpanded() => IsExpanded = !IsExpanded;
    private void OnPresentationChanged()
    {
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(AccessibilityName));
    }
}
