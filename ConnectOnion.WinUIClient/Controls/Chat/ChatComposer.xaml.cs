using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.Services.Attachments;
using Microsoft.Extensions.Logging;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>Text plus whatever attachments were ready to send at submit time, and the approval
/// mode the composer was showing when the user pressed send.
///
/// <para><paramref name="Mode"/> exists for the one submission that crosses a page boundary: the
/// first message to an agent is composed on <c>AgentDetailPage</c>, which has no conversation yet,
/// and the conversation it creates would otherwise open at the default mode no matter what the
/// picker said. Optional so the two callers that build a submission from a bare prompt keep
/// compiling; <c>ChatPage</c>'s own composer already owns its mode through
/// <c>ModeChangeRequested</c> and ignores this.</para></summary>
public sealed record ComposerSubmission(
    string Text,
    IReadOnlyList<PendingAttachment> Attachments,
    string Mode = AgentModes.Safe);

/// <summary>A quick prompt's visible chip label and the fuller draft inserted when selected.</summary>
internal sealed class ComposerSuggestion
{
    public ComposerSuggestion(string label, string prompt)
    {
        Label = label;
        Prompt = prompt;
    }

    public string Label { get; }
    public string Prompt { get; }
}

public sealed partial class ChatComposer : UserControl, IDisposable
{
    private static readonly TimeSpan MinimumStopFeedbackDuration = TimeSpan.FromMilliseconds(500);
    // The attachment pipeline swallows its failures so a bad file cannot take the composer down.
    // These are what stops "I picked a file and nothing happened" from being untraceable; the
    // successful steps are deliberately not logged, because a working attachment reports itself
    // on screen. WinUI constructs this control, so the logger comes from AppServices rather than
    // a constructor parameter.
    private static readonly Action<ILogger, string, Exception?> LogAttachmentRestoreFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "AttachmentRestoreFailed"),
            "Draft attachment {FileName} could not be restored and was dropped");

    private static readonly Action<ILogger, string, Exception?> LogAttachmentPickFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, "AttachmentPickFailed"),
            "Picking a {Kind} attachment failed");

    private static readonly Action<ILogger, Exception?> LogAttachmentDropFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(3, "AttachmentDropFailed"),
            "A dropped attachment could not be accepted");

    private static ILogger Log => AppServices.Logging.CreateLogger<ChatComposer>();

    public static readonly DependencyProperty CanSubmitProperty =
        DependencyProperty.Register(
            nameof(CanSubmit),
            typeof(bool),
            typeof(ChatComposer),
            new PropertyMetadata(true, OnCanSubmitChanged));

    /// <summary>Sending is blocked, but composing is not.
    ///
    /// <para>Distinct from <see cref="CanSubmit"/> on purpose. <c>CanSubmit</c> means "this
    /// composer is not usable" and disables the text box, the attachment picker and the mic;
    /// this means "you may write, you just cannot send it right now" — which is the state while
    /// the agent is parked on an interactive card, since the answer has to go through that card.
    /// </para>
    ///
    /// <para>Routing that state through <c>CanSubmit</c> instead had a visible side effect worth
    /// not repeating: disabling a focused <c>TextBox</c> makes WinUI move focus to the next
    /// enabled sibling, which in this composer is the approval-mode button (it deliberately stays
    /// enabled mid-turn). So every approval that arrived while the user's caret was in the box
    /// drew a focus ring around "Safe" — a control nobody had touched appearing to be selected.
    /// </para></summary>
    public static readonly DependencyProperty IsSubmitBlockedProperty =
        DependencyProperty.Register(
            nameof(IsSubmitBlocked),
            typeof(bool),
            typeof(ChatComposer),
            new PropertyMetadata(false, OnCanSubmitChanged));

    public static readonly DependencyProperty EnterToSubmitProperty =
        DependencyProperty.Register(
            nameof(EnterToSubmit),
            typeof(bool),
            typeof(ChatComposer),
            new PropertyMetadata(true));

    public static readonly DependencyProperty IsBusyProperty =
        DependencyProperty.Register(
            nameof(IsBusy),
            typeof(bool),
            typeof(ChatComposer),
            new PropertyMetadata(false, OnIsBusyChanged));

    public static readonly DependencyProperty AcceptedInputsProperty =
        DependencyProperty.Register(
            nameof(AcceptedInputs),
            typeof(AgentAcceptedInputs),
            typeof(ChatComposer),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ShowSuggestionsProperty =
        DependencyProperty.Register(
            nameof(ShowSuggestions),
            typeof(bool),
            typeof(ChatComposer),
            new PropertyMetadata(true, OnShowSuggestionsChanged));

    /// <summary>
    /// The agent's declared skills, from its <c>/info</c>. Set it and the composer offers them as
    /// slash-command completions; leave it null and nothing about the composer changes.
    /// </summary>
    public static readonly DependencyProperty SkillsProperty =
        DependencyProperty.Register(
            nameof(Skills),
            typeof(IReadOnlyList<SkillInfo>),
            typeof(ChatComposer),
            new PropertyMetadata(null, OnSkillsChanged));

    /// <summary>
    /// The running turn's spend, rendered beside the connection status. Empty collapses the slot.
    /// </summary>
    public static readonly DependencyProperty UsageTextProperty =
        DependencyProperty.Register(
            nameof(UsageText),
            typeof(string),
            typeof(ChatComposer),
            new PropertyMetadata("", OnUsageTextChanged));

    /// <summary>
    /// Ready-made prompts to offer above the input before the generic starters. Separate from
    /// <see cref="Skills"/> because these are already-phrased sentences the caller chose (see
    /// <c>AgentSkills.BestOffers</c>), not names to complete.
    /// </summary>
    public static readonly DependencyProperty SuggestionPromptsProperty =
        DependencyProperty.Register(
            nameof(SuggestionPrompts),
            typeof(IReadOnlyList<string>),
            typeof(ChatComposer),
            new PropertyMetadata(null, OnShowSuggestionsChanged));

    public static readonly DependencyProperty ShowModeSelectorProperty =
        DependencyProperty.Register(
            nameof(ShowModeSelector),
            typeof(bool),
            typeof(ChatComposer),
            new PropertyMetadata(false, OnModeChanged));

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(
            nameof(Mode),
            typeof(string),
            typeof(ChatComposer),
            new PropertyMetadata(AgentModes.Safe, OnModeChanged));

    public static readonly DependencyProperty CanStopProperty =
        DependencyProperty.Register(
            nameof(CanStop),
            typeof(bool),
            typeof(ChatComposer),
            new PropertyMetadata(false, OnIsBusyChanged));

    public static readonly DependencyProperty IsStoppingProperty =
        DependencyProperty.Register(
            nameof(IsStopping),
            typeof(bool),
            typeof(ChatComposer),
            new PropertyMetadata(false, OnIsBusyChanged));

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(
            nameof(PlaceholderText),
            typeof(string),
            typeof(ChatComposer),
            new PropertyMetadata(LocalizedStrings.Get("ComposerWriteMessage", "Write a message...")));

    public static readonly DependencyProperty ShowConnectionStatusProperty =
        DependencyProperty.Register(
            nameof(ShowConnectionStatus),
            typeof(bool),
            typeof(ChatComposer),
            new PropertyMetadata(false));

    public static readonly DependencyProperty ConnectionPhaseProperty =
        DependencyProperty.Register(
            nameof(ConnectionPhase),
            typeof(ConnectOnion.WinUIClient.Models.ConnectionPhase),
            typeof(ChatComposer),
            new PropertyMetadata(ConnectOnion.WinUIClient.Models.ConnectionPhase.Idle));

    public static readonly DependencyProperty ConnectionStatusTextProperty =
        DependencyProperty.Register(
            nameof(ConnectionStatusText),
            typeof(string),
            typeof(ChatComposer),
            new PropertyMetadata(""));

    public event EventHandler<ComposerSubmission>? SubmitRequested;

    /// <summary>The stop button was pressed. The host decides what that can achieve; the composer
    /// reports the press and keeps a short local feedback latch so a fast terminal update cannot
    /// erase the acknowledgement before the user sees it.</summary>
    public event EventHandler? StopRequested;

    /// <summary>The user picked an approval mode from the pill. Carries the wire value
    /// (<see cref="AgentModes"/>), not the menu label.</summary>
    public event EventHandler<string>? ModeChangeRequested;

    /// <summary>Attachments picked but not yet sent. Cleared (for the ones actually sent) on submit.</summary>
    public ObservableCollection<PendingAttachment> PendingAttachments { get; } = new();

    private CancellationTokenSource _lifetimeCts = new();
    private bool _showStopFeedback;
    private int _disposed;

    public ChatComposer()
    {
        InitializeComponent();
        // TextBox's internal context-request handling can mark the routed event before an
        // ordinary XAML handler sees it. handledEventsToo guarantees mouse right-click,
        // Shift+F10, the Menu key, and touch long-press all reach the explicit edit menu.
        InputBox.AddHandler(
            UIElement.ContextRequestedEvent,
            new Windows.Foundation.TypedEventHandler<UIElement, ContextRequestedEventArgs>(
                InputBox_ContextRequested),
            handledEventsToo: true);
        ApplyWaveformTheme();
        _waveformTimer.Tick += WaveformTimer_Tick;
        _recordingElapsedTimer.Tick += RecordingElapsedTimer_Tick;
        ActualThemeChanged += Composer_ActualThemeChanged;
        Loaded += Composer_Loaded;
        Unloaded += Composer_Unloaded;
        PendingAttachments.CollectionChanged += PendingAttachments_CollectionChanged;
        RefreshSendButtonState();
    }

    /// <summary>Starts a fresh lifetime for work initiated while this composer is loaded.</summary>
    private void Composer_Loaded(object sender, RoutedEventArgs e)
    {
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _lifetimeCts = new CancellationTokenSource();
    }

    private void Composer_ActualThemeChanged(FrameworkElement sender, object args)
        => ApplyWaveformTheme();

    private void PendingAttachments_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        AttachmentsScrollViewer.Visibility = PendingAttachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshSendButtonState();
    }

    public bool CanSubmit
    {
        get => (bool)GetValue(CanSubmitProperty);
        set => SetValue(CanSubmitProperty, value);
    }

    public bool IsSubmitBlocked
    {
        get => (bool)GetValue(IsSubmitBlockedProperty);
        set => SetValue(IsSubmitBlockedProperty, value);
    }

    public bool EnterToSubmit
    {
        get => (bool)GetValue(EnterToSubmitProperty);
        set => SetValue(EnterToSubmitProperty, value);
    }

    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    /// <summary>The connected agent's advertised input capabilities, from <c>/info</c>. Null while unknown (not yet fetched, or offline) — attachments are not blocked in that case.</summary>
    public AgentAcceptedInputs? AcceptedInputs
    {
        get => (AgentAcceptedInputs?)GetValue(AcceptedInputsProperty);
        set => SetValue(AcceptedInputsProperty, value);
    }

    /// <summary>
    /// Whether the "What can you do?" / "Show system info" / "List files" quick
    /// prompt chips appear above the input box. True by default (the agent-detail
    /// page's first-message composer); ChatPage sets this false since those
    /// starter prompts don't make sense once a conversation is already underway.
    /// </summary>
    public bool ShowSuggestions
    {
        get => (bool)GetValue(ShowSuggestionsProperty);
        set => SetValue(ShowSuggestionsProperty, value);
    }

    /// <summary>The agent's declared skills, offered as <c>/</c> completions. Null or empty leaves
    /// the composer exactly as it behaves without a palette.</summary>
    public IReadOnlyList<SkillInfo>? Skills
    {
        get => (IReadOnlyList<SkillInfo>?)GetValue(SkillsProperty);
        set => SetValue(SkillsProperty, value);
    }

    /// <summary>Prompts shown first as chips above the input. The generic starters fill any slots
    /// left in the three-chip row.</summary>
    public IReadOnlyList<string>? SuggestionPrompts
    {
        get => (IReadOnlyList<string>?)GetValue(SuggestionPromptsProperty);
        set => SetValue(SuggestionPromptsProperty, value);
    }

    /// <summary>The running turn's spend ("3.1K→157 tok · ctx 1.3% · 1 tools"). Empty hides it.</summary>
    public string UsageText
    {
        get => (string)GetValue(UsageTextProperty);
        set => SetValue(UsageTextProperty, value);
    }

    /// <summary>Whether there is anything to show. A separate property rather than a
    /// string-to-visibility converter so the slot's rule lives beside the value it describes.</summary>
    public bool HasUsageText => !string.IsNullOrEmpty(UsageText);

    private static void OnUsageTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // HasUsageText is derived, and a plain CLR property raises nothing on its own — without
        // this the slot would never appear after the first usage report.
        if (d is ChatComposer composer) composer.Bindings.Update();
    }

    private static void OnSkillsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Skills usually arrive after the page loads (the /info fetch is async), so a draft the
        // user already started typing has to be re-evaluated rather than waiting for the next
        // keystroke to open the palette.
        if (d is ChatComposer composer) composer.RefreshSkillPalette();
    }

    /// <summary>Whether the approval-mode pill is shown. Off by default: it belongs to an ongoing
    /// conversation (ChatPage), not to the agent-detail page's one-shot first message.</summary>
    public bool ShowModeSelector
    {
        get => (bool)GetValue(ShowModeSelectorProperty);
        set => SetValue(ShowModeSelectorProperty, value);
    }

    /// <summary>The conversation's current approval mode, one of <see cref="AgentModes"/>.</summary>
    public string Mode
    {
        get => (string)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>INPUT has reached the socket and the turn has not reached OUTPUT/ERROR.</summary>
    public bool CanStop
    {
        get => (bool)GetValue(CanStopProperty);
        set => SetValue(CanStopProperty, value);
    }

    /// <summary>A stop request was sent and the active run has not settled yet.</summary>
    public bool IsStopping
    {
        get => (bool)GetValue(IsStoppingProperty);
        set => SetValue(IsStoppingProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>Whether the compact connection indicator is rendered inside the composer.</summary>
    public bool ShowConnectionStatus
    {
        get => (bool)GetValue(ShowConnectionStatusProperty);
        set => SetValue(ShowConnectionStatusProperty, value);
    }

    /// <summary>The current transport phase used to color the composer's status indicator.</summary>
    public ConnectionPhase ConnectionPhase
    {
        get => (ConnectionPhase)GetValue(ConnectionPhaseProperty);
        set => SetValue(ConnectionPhaseProperty, value);
    }

    /// <summary>The user-facing transport status shown beside the indicator.</summary>
    public string ConnectionStatusText
    {
        get => (string)GetValue(ConnectionStatusTextProperty);
        set => SetValue(ConnectionStatusTextProperty, value);
    }

    public void FocusInput()
        => InputBox.Focus(FocusState.Programmatic);

    /// <summary>
    /// Moves focus to the draft when <paramref name="button"/> currently holds it and is about to
    /// be collapsed.
    ///
    /// <para>Hiding the focused element does not leave focus nowhere — the framework relocates it,
    /// and the nearest preceding tab stop in this composer is the approval-mode drop-down. That is
    /// how sending a message ended up parking focus on a control that sends <c>mode_change</c>
    /// frames to the agent when you press Enter on it.</para>
    ///
    /// <para>Called <i>before</i> the visibility is applied, on purpose: once the element is
    /// collapsed the framework has already chosen a new focus target and putting it back would be
    /// a second visible jump rather than a prevention.</para>
    /// </summary>
    private void ReturnFocusToDraftIfLeaving(Control button, bool willBeVisible)
    {
        if (willBeVisible || button.Visibility != Visibility.Visible) return;
        if (XamlRoot is null) return;
        if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), button)) return;

        InputBox.Focus(FocusState.Programmatic);
    }

    private void InputBox_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        ComposerEditMenu.ShowAt(InputBox);
        e.Handled = true;
    }

    private void ComposerEditMenu_Opening(object sender, object e)
    {
        var canEdit = InputBox.IsEnabled && !InputBox.IsReadOnly;
        var hasSelection = InputBox.SelectionLength > 0;
        ComposerUndoMenuItem.IsEnabled = canEdit && InputBox.CanUndo;
        ComposerRedoMenuItem.IsEnabled = canEdit && InputBox.CanRedo;
        ComposerCutMenuItem.IsEnabled = canEdit && hasSelection;
        ComposerCopyMenuItem.IsEnabled = hasSelection;
        ComposerPasteMenuItem.IsEnabled = canEdit && InputBox.CanPasteClipboardContent;
        ComposerSelectAllMenuItem.IsEnabled = InputBox.Text.Length > 0;
    }

    private void ComposerUndo_Click(object sender, RoutedEventArgs e) => InputBox.Undo();

    private void ComposerRedo_Click(object sender, RoutedEventArgs e) => InputBox.Redo();

    private void ComposerCut_Click(object sender, RoutedEventArgs e)
        => InputBox.CutSelectionToClipboard();

    private void ComposerCopy_Click(object sender, RoutedEventArgs e)
        => InputBox.CopySelectionToClipboard();

    private void ComposerPaste_Click(object sender, RoutedEventArgs e)
        => InputBox.PasteFromClipboard();

    private void ComposerSelectAll_Click(object sender, RoutedEventArgs e) => InputBox.SelectAll();

    /// <summary>Replaces the current draft and focuses the editor without submitting it.</summary>
    public void SetDraft(string text)
    {
        InputBox.Text = text;
        InputBox.SelectionStart = InputBox.Text.Length;
        RefreshSendButtonState();
        FocusInput();
    }

    /// <summary>
    /// Puts a submitted message back on the composer after the send was cancelled before it
    /// reached the agent.
    ///
    /// <para>Attachments retain metadata and a local path only. Wire encoding is deferred until
    /// Send, so restoring a draft never allocates a Base64 copy. Anything whose file has since
    /// gone is dropped rather than restored as a chip that would fail on submit.</para>
    ///
    /// <para>Appends to whatever is already there instead of replacing it — the user may well have
    /// started typing again in the moment between submitting and cancelling, and that text is
    /// theirs, not ours to discard.</para>
    /// </summary>
    public async Task RestoreDraftAsync(string text, IReadOnlyList<PendingAttachment> attachments)
    {
        if (!string.IsNullOrEmpty(text))
        {
            InputBox.Text = InputBox.Text.Length == 0 ? text : text + "\n" + InputBox.Text;
            InputBox.SelectionStart = InputBox.Text.Length;
        }

        foreach (var attachment in attachments)
        {
            if (string.IsNullOrEmpty(attachment.LocalPath) || !File.Exists(attachment.LocalPath)) continue;
            try
            {
                attachment.Status = AttachmentStatus.Ready;
                PendingAttachments.Add(attachment);
            }
            catch (Exception ex)
            {
                // A file that has moved or become unreadable simply does not come back. Restoring
                // it as a chip that cannot be sent would be worse than losing it quietly here.
                LogAttachmentRestoreFailed(Log, attachment.FileName, ex);
            }
        }

        RefreshSendButtonState();
        FocusInput();
    }

    private static void OnCanSubmitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ChatComposer)d).RefreshSendButtonState();

    private static void OnIsBusyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ChatComposer)d).RefreshSendButtonState();

    private static void OnShowSuggestionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var composer = (ChatComposer)d;
        composer.ApplySuggestionVisibility(composer.ActualWidth);
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ChatComposer)d).RefreshModeSelector();

    private void RefreshModeSelector()
    {
        ModeButton.Visibility = ShowModeSelector ? Visibility.Visible : Visibility.Collapsed;
        if (!ShowModeSelector) return;

        var mode = Mode;
        ModeLabel.Text = AgentModes.DisplayName(mode);
        var shortcut = AppServices.Shortcuts
            .GetChord(KeyboardShortcutCatalog.Ids.CycleChatMode)
            .ToBinding()
            .DisplayText;
        var modeHint = LocalizedStrings.Format(
            "ComposerModeButtonName",
            "Approval mode: {0}. Shortcut: {1}",
            ModeLabel.Text,
            shortcut);
        AutomationProperties.SetName(ModeButton, modeHint);
        ToolTipService.SetToolTip(ModeButton, modeHint);
        // Radio state is driven from the property, never from the click: the agent can change mode
        // on its own (enter_plan_mode), and when it does the pill has to follow it rather than keep
        // showing whatever the user last picked.
        ModeSafeItem.IsChecked = mode == AgentModes.Safe;
        ModeAcceptEditsItem.IsChecked = mode == AgentModes.AcceptEdits;
        ModePlanItem.IsChecked = mode == AgentModes.Plan;
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string mode || mode == Mode)
        {
            RefreshModeSelector();
            return;
        }
        // Not applied locally: the page owns the mode and will set it back on us once it has been
        // persisted and pushed, so the pill can never claim a mode the agent was never told about.
        ModeChangeRequested?.Invoke(this, mode);
        RefreshModeSelector();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStop || _showStopFeedback) return;

        _showStopFeedback = true;
        RefreshSendButtonState();
        try
        {
            StopRequested?.Invoke(this, EventArgs.Empty);
            await Task.Delay(MinimumStopFeedbackDuration, _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Navigation/unload ends visual feedback; the run itself remains owned by the VM.
        }
        finally
        {
            _showStopFeedback = false;
            if (Volatile.Read(ref _disposed) == 0) RefreshSendButtonState();
        }
    }

    /// <summary>Keeps the agent's skill offers first, then fills the three-chip row with generic
    /// starters. The containing horizontal rail keeps onboarding reachable at narrow widths.</summary>
    private void ApplySuggestionVisibility(double width)
    {
        var offers = FilterSuggestionsForActiveLanguage(SuggestionPrompts);
        var genericSuggestions = GenericSuggestions();
        var labels = AgentSkills.CompleteOffers(
            offers,
            genericSuggestions.Select(suggestion => suggestion.Label).ToList());
        var genericByLabel = genericSuggestions.ToDictionary(
            suggestion => suggestion.Label,
            StringComparer.OrdinalIgnoreCase);
        var suggestions = labels
            .Select(label => genericByLabel.TryGetValue(label, out var generic)
                ? generic
                : new ComposerSuggestion(label, label))
            .ToList();

        SuggestionList.ItemsSource = suggestions;
        SuggestionList.Visibility = ShowSuggestions && suggestions.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        SuggestionScroller.Visibility = SuggestionList.Visibility;
    }

    private static IReadOnlyList<ComposerSuggestion> GenericSuggestions()
        =>
        (ComposerSuggestion[])
        [
            new(
                LocalizedStrings.Get(
                    "ComposerSuggestionCapabilitiesLabel",
                    "Summarize your capabilities"),
                LocalizedStrings.Get(
                    "ComposerSuggestionCapabilitiesPrompt",
                    "Summarize your capabilities and give me a few concrete examples.")),
            new(
                LocalizedStrings.Get(
                    "ComposerSuggestionSystemLabel",
                    "Help me get started"),
                LocalizedStrings.Get(
                    "ComposerSuggestionSystemPrompt",
                    "Help me get started. What information do you need from me?")),
            new(
                LocalizedStrings.Get(
                    "ComposerSuggestionFilesLabel",
                    "Suggest three useful tasks"),
                LocalizedStrings.Get(
                    "ComposerSuggestionFilesPrompt",
                    "Suggest three useful tasks you can help me complete.")),
        ];

    private static IReadOnlyList<string>? FilterSuggestionsForActiveLanguage(
        IReadOnlyList<string>? offers)
    {
        if (offers is not { Count: > 0 } ||
            !string.Equals(
                CultureInfo.CurrentUICulture.Name,
                Services.LanguagePreferenceStore.SimplifiedChinese,
                StringComparison.OrdinalIgnoreCase))
        {
            return offers;
        }

        var localized = offers.Where(ContainsCjkCharacter).ToList();
        return localized.Count > 0 ? localized : null;
    }

    private static bool ContainsCjkCharacter(string text)
        => text.Any(character =>
            character is >= '\u3400' and <= '\u4DBF' or
            >= '\u4E00' and <= '\u9FFF');

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingState == RecordingState.Recording)
            await FinishVoiceInputAsync();
        else if (IsVoiceInputActive)
            return;
        SubmitCurrentText();
    }

    private void Composer_Unloaded(object sender, RoutedEventArgs e)
    {
        // Cancel the active work when the control leaves the tree. Full Dispose remains the
        // explicit shutdown/teardown path; Loaded renews the lifetime token if this control is
        // temporarily detached and reattached.
        _lifetimeCts.Cancel();
        _waveformTimer.Stop();
        CancelVoiceInput();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Unloaded -= Composer_Unloaded;
        Loaded -= Composer_Loaded;
        ActualThemeChanged -= Composer_ActualThemeChanged;
        PendingAttachments.CollectionChanged -= PendingAttachments_CollectionChanged;
        _waveformTimer.Tick -= WaveformTimer_Tick;
        _waveformTimer.Stop();
        _recordingElapsedTimer.Tick -= RecordingElapsedTimer_Tick;
        _recordingElapsedTimer.Stop();

        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _recordingCts?.Cancel();
        _recordingCts?.Dispose();
        _recordingCts = null;

        StopAudioMeter(discardAudio: true);
        _voiceCapture.Dispose();
        WaveformCanvas.Children.Clear();
        _waveformBars.Clear();
    }

    // ---- Attachments ----

    // Built once and reused. Rebuilding a MenuFlyout (with fresh lambda Click handlers) on every
    // click leaked: WinUI 3 does not reliably release a Popup/MenuFlyout once shown, so a new one
    // per open accumulated for the process's life. The two items' enabled state is the only thing
    // that varies per open (with the agent's accepted inputs), so that is refreshed in place.
    private MenuFlyout? _attachmentFlyout;
    private MenuFlyoutItem? _addImageItem;
    private MenuFlyoutItem? _addFileItem;

    private void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (_attachmentFlyout is null)
        {
            _addImageItem = new MenuFlyoutItem
            {
                Text = Common.LocalizedStrings.Get("ComposerAddImage", "Add image...")
            };
            _addImageItem.Click += async (_, _) => await AddAttachmentsAsync(AttachmentKind.Image);

            _addFileItem = new MenuFlyoutItem
            {
                Text = Common.LocalizedStrings.Get("ComposerAddFile", "Add file...")
            };
            _addFileItem.Click += async (_, _) => await AddAttachmentsAsync(AttachmentKind.File);

            _attachmentFlyout = new MenuFlyout();
            _attachmentFlyout.Items.Add(_addImageItem);
            _attachmentFlyout.Items.Add(_addFileItem);
        }

        var accepted = AcceptedInputs;

        var imagesBlocked = accepted?.Images == false;
        _addImageItem!.IsEnabled = !imagesBlocked;
        ToolTipService.SetToolTip(_addImageItem, imagesBlocked ? "This agent does not accept image input." : null);

        var filesBlocked = accepted is not null && accepted.Files is null;
        _addFileItem!.IsEnabled = !filesBlocked;
        ToolTipService.SetToolTip(_addFileItem, filesBlocked ? "This agent does not accept file input." : null);

        _attachmentFlyout.ShowAt((FrameworkElement)sender);
    }

    private async Task AddAttachmentsAsync(AttachmentKind kind)
    {
        IReadOnlyList<PendingAttachment> picked;
        try
        {
            picked = await AttachmentPickerService.PickAsync(kind, _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            LogAttachmentPickFailed(Log, kind.ToString(), ex);
            // The user clicked "+", chose a file, and the picker threw. There is no attachment
            // chip to hang this on, so without the bar the click would look like a no-op.
            ShowComposerError(Common.LocalizedStrings.Get(
                "ComposerAttachmentPickFailure",
                "Could not add that attachment. Please try again."));
            return;
        }

        // Empty result means the user dismissed the picker — a normal outcome, not an error.
        ClearComposerError();
        AddAttachments(picked);
    }

    /// <summary>Shows a composer-level failure. Used only where no <see cref="PendingAttachment"/>
    /// exists to carry the message on its own chip.</summary>
    private void ShowComposerError(string message)
    {
        ComposerErrorBar.Message = message;
        ComposerErrorBar.IsOpen = true;
    }

    private void ClearComposerError() => ComposerErrorBar.IsOpen = false;

    /// <summary>
    /// Validates each candidate and marks the ones that pass ready for send-time encoding. Shared by the
    /// file picker and drag-and-drop, so both routes get identical capability gating — the
    /// only difference between them is how the <see cref="PendingAttachment"/> list was
    /// produced. Rejected candidates are still added to the rail (as Failed) so the user sees
    /// *why* a file didn't take.
    /// </summary>
    private void AddAttachments(IReadOnlyList<PendingAttachment> candidates)
    {
        foreach (var attachment in candidates)
        {
            // Counted per-kind and re-read each iteration: a mixed drop of images and files
            // is checked against each kind's own count limit, including the ones just added.
            var existingCount = PendingAttachments.Count(p => p.Kind == attachment.Kind);
            var error = AttachmentValidationService.Validate(attachment, AcceptedInputs, existingCount);

            // Status is settled *before* the Add, which is the whole point. Adding first fires
            // CollectionChanged — the only thing that recomputes the send button — while the
            // status is still Pending (the enum's zero value), and nothing observes the item's
            // own PropertyChanged afterwards. So an attachment added with an empty draft left
            // `hasReadyAttachment` reading false and the send button disabled until the user
            // typed a character. Enter still worked, because SubmitCurrentText re-reads the
            // collection rather than trusting the button, which is what made it look like a
            // rendering glitch rather than stale state.
            attachment.Status = error is null ? AttachmentStatus.Ready : AttachmentStatus.Failed;
            // Not logged: the rejection reason is already on the chip in front of the user.
            attachment.Error = error;

            PendingAttachments.Add(attachment);
        }

        // Belt and braces, and deliberately not redundant: the line above keeps CollectionChanged
        // truthful, this keeps the method correct even if a future edit reorders it. The restore
        // path (RestoreSubmission) has always done both.
        RefreshSendButtonState();
    }

    // ---- Drag and drop ----

    private void Composer_DragEnter(object sender, DragEventArgs e)
        => UpdateDragFeedback(e);

    private void Composer_DragOver(object sender, DragEventArgs e)
        => UpdateDragFeedback(e);

    private void Composer_DragLeave(object sender, DragEventArgs e)
        => DropOverlay.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Runs on every pointer move during a drag, so it stays synchronous — inspecting the
    /// payload's *format* is cheap, whereas actually materializing the storage items is not
    /// and is deferred to <see cref="Composer_Drop"/>.
    /// </summary>
    private void UpdateDragFeedback(DragEventArgs e)
    {
        if (!CanAcceptDrop(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is { } dragUi)
        {
            dragUi.Caption = "Attach";
            dragUi.IsCaptionVisible = true;
            dragUi.IsGlyphVisible = false;
        }
        DropOverlay.Visibility = Visibility.Visible;

        // Claims the drag: the event still bubbles to MainWindow (which listens with
        // handledEventsToo), where Handled is the signal to drop the window-wide "drop it on
        // the message box" hint now that the pointer has reached the real target.
        e.Handled = true;
    }

    /// <summary>
    /// Whether the payload is droppable at all. Deliberately does not check per-file
    /// capability or type — the dropped files aren't readable from here — so an unsupported
    /// file still lands in the rail and gets a visible rejection from
    /// <see cref="AttachmentValidationService"/> rather than being refused by a cursor the
    /// user can't interrogate.
    /// </summary>
    private bool CanAcceptDrop(DataPackageView data)
        => CanSubmit && !IsVoiceInputActive && AttachmentDropService.ContainsFiles(data);

    private async void Composer_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!CanAcceptDrop(e.DataView)) return;

        // The drag source stays blocked until the deferral completes, so it must be released
        // on every path out of this handler.
        var deferral = e.GetDeferral();
        try
        {
            var dropped = await AttachmentDropService.ExtractAsync(e.DataView, _lifetimeCts.Token);
            ClearComposerError();
            AddAttachments(dropped);
        }
        catch (OperationCanceledException)
        {
            // Composer unloaded mid-drop; the attachments are being discarded anyway.
        }
        catch (Exception ex)
        {
            LogAttachmentDropFailed(Log, ex);
            // Nothing was extracted, so there is no chip to mark Failed. A drop that vanishes
            // silently reads as the composer refusing the file for an unstated reason.
            ShowComposerError(Common.LocalizedStrings.Get(
                "ComposerAttachmentDropFailure",
                "Could not read the dropped files."));
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PendingAttachment attachment)
        {
            PendingAttachments.Remove(attachment);
        }
    }


    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string prompt) return;
        SetDraft(prompt);
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshSendButtonState();
        RefreshSkillPalette();
    }

    private void InputBox_GotFocus(object sender, RoutedEventArgs e)
        => Diagnostics.StartupTelemetry.Mark(Diagnostics.StartupPhases.FirstInteractive);

    private async void Composer_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsVoiceInputActive) return;

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CancelVoiceInput();
            return;
        }

        var isShiftDown = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (_recordingState == RecordingState.Recording
            && e.Key == VirtualKey.Enter
            && !isShiftDown)
        {
            e.Handled = true;
            await FinishVoiceInputAsync();
        }
    }

    private void InputBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // The palette gets first refusal on every key it uses. It has to: while it is open, Enter
        // means "complete this command", and letting the submit rule below see that key first
        // would send "/pos" as a message the moment the user tried to accept "/post".
        if (IsSkillPaletteOpen && HandleSkillPaletteKey(e.Key)) { e.Handled = true; return; }

        var isShiftDown = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (EnterToSubmit && e.Key == VirtualKey.Enter && !isShiftDown)
        {
            e.Handled = true;
            SubmitCurrentText();
        }
    }

    // ---- Slash-command palette ---------------------------------------------
    //
    // The agent's declared skills, offered as completions while the user is typing a "/" command.
    // The matching rules live in Core (AgentSkills) because they are the testable half; this is
    // only the keyboard and visibility plumbing.

    private bool IsSkillPaletteOpen => SkillPalette.Visibility == Visibility.Visible;

    /// <summary>Opens, filters or closes the palette for the current draft text.</summary>
    private void RefreshSkillPalette()
    {
        if (Skills is not { Count: > 0 } skills
            || !AgentSkills.TryGetSlashQuery(InputBox.Text, out var query))
        {
            CloseSkillPalette();
            return;
        }

        var matches = AgentSkills.Match(skills, query);
        if (matches.Count == 0)
        {
            // A query that matches nothing closes the palette rather than showing an empty box:
            // an empty popup over the input says less than the input itself, and keeping it open
            // would go on stealing Enter for a command that does not exist.
            CloseSkillPalette();
            return;
        }

        SkillList.ItemsSource = matches;
        // Reset to the top on every keystroke: the list has just been re-ranked, so a preserved
        // index would point at whatever happens to sit in that slot now.
        SkillList.SelectedIndex = 0;
        SkillPalette.Visibility = Visibility.Visible;
    }

    private void CloseSkillPalette()
    {
        if (SkillPalette.Visibility == Visibility.Collapsed) return;
        SkillPalette.Visibility = Visibility.Collapsed;
        // Dropped rather than left in place: the list holds the agent's skills, and a closed
        // palette has no reason to keep a realized item container per skill alive.
        SkillList.ItemsSource = null;
    }

    /// <summary>Handles the keys the palette owns while it is open. Returns false for anything
    /// else, so ordinary typing (and the Enter-to-submit rule) is untouched.</summary>
    private bool HandleSkillPaletteKey(VirtualKey key)
    {
        var count = SkillList.Items.Count;
        if (count == 0) return false;

        switch (key)
        {
            case VirtualKey.Down:
                SkillList.SelectedIndex = (SkillList.SelectedIndex + 1) % count;
                SkillList.ScrollIntoView(SkillList.SelectedItem);
                return true;

            case VirtualKey.Up:
                SkillList.SelectedIndex = (SkillList.SelectedIndex - 1 + count) % count;
                SkillList.ScrollIntoView(SkillList.SelectedItem);
                return true;

            case VirtualKey.Tab:
            case VirtualKey.Enter:
                CompleteSkill(SkillList.SelectedItem as SkillInfo);
                return true;

            case VirtualKey.Escape:
                // Closes the palette and leaves the text alone. The reference client clears the
                // draft here; discarding what someone typed because they dismissed a popup is a
                // worse trade than making them press Backspace.
                CloseSkillPalette();
                return true;

            default:
                return false;
        }
    }

    private void SkillList_ItemClick(object sender, ItemClickEventArgs e)
        => CompleteSkill(e.ClickedItem as SkillInfo);

    /// <summary>Completes the draft to <c>/name </c> — with the trailing space, which is what
    /// closes the palette (see <c>AgentSkills.TryGetSlashQuery</c>) and puts the caret where the
    /// command's arguments go.</summary>
    private void CompleteSkill(SkillInfo? skill)
    {
        if (skill is null) return;
        SetDraft($"/{skill.Name} ");
        CloseSkillPalette();
        InputBox.Focus(FocusState.Programmatic);
    }

    private void SubmitCurrentText()
    {
        if (IsVoiceInputActive) return;

        var text = InputBox.Text.Trim();
        var isAttachmentsBusy = PendingAttachments.Any(a => a.IsBusy);
        var readyAttachments = PendingAttachments.Where(a => a.Status == AttachmentStatus.Ready).ToList();

        if (!CanSubmit || IsSubmitBlocked || isAttachmentsBusy) return;
        if (text.Length == 0 && readyAttachments.Count == 0) return;

        InputBox.Text = "";
        // Only the attachments that made it to Ready are sent; Failed ones stay in
        // the rail so the user notices and can remove/retry them — a bad attachment
        // must never silently disappear or block the rest of the message.
        foreach (var attachment in readyAttachments) PendingAttachments.Remove(attachment);
        RefreshSendButtonState();

        // Claim focus before anything can take it. A click leaves focus on the send button, and
        // the turn that starts a moment later collapses that button — at which point the
        // framework has to move focus somewhere and picks the nearest preceding tab stop, which
        // is the approval-mode drop-down (column 2; send/stop/finish all share column 4). Focus
        // was being *evicted* rather than assigned, which is why it landed somewhere nobody
        // chose, and a reflexive Enter there opens the mode menu — a real mode_change to the
        // agent. Returning the caret to the draft is also just what a chat composer should do.
        //
        // Ordered before SubmitRequested deliberately: this is synchronous, while IsBusy flips
        // through the view model and back via the dispatcher, so by the time the button collapses
        // focus already lives somewhere that is not going away.
        FocusInput();

        SubmitRequested?.Invoke(this, new ComposerSubmission(text, readyAttachments, Mode));
    }


    private void RefreshSendButtonState()
    {
        var isStarting = _recordingState == RecordingState.Starting;
        var isRecording = _recordingState == RecordingState.Recording;
        var isTranscribing = _recordingState == RecordingState.Transcribing;
        var isActive = IsVoiceInputActive;

        var isAttachmentsBusy = PendingAttachments.Any(a => a.IsBusy);
        var hasReadyAttachment = PendingAttachments.Any(a => a.Status == AttachmentStatus.Ready);
        // IsSubmitBlocked gates only this — the draft stays editable, so the three lines below
        // still read CanSubmit alone and a focused text box is never disabled out from under the
        // caret. See the IsSubmitBlockedProperty remarks.
        var canSend = CanSubmit && !IsSubmitBlocked && !isAttachmentsBusy
            && (!string.IsNullOrWhiteSpace(InputBox.Text) || hasReadyAttachment);
        // Match the web composer: typing and Enter remain available during a turn.
        // ChatViewModel routes that submission onto the existing socket as runtime INPUT.
        InputBox.IsEnabled = CanSubmit;
        AddAttachmentButton.IsEnabled = CanSubmit && !isActive;
        SpeechButton.IsEnabled = CanSubmit && !isActive;

        // Send and Stop share the slot: a turn is running or it isn't.
        var showSend = !(isActive || IsBusy || _showStopFeedback);
        var showStopButton = (CanStop || IsStopping || _showStopFeedback) && !isActive;

        // Whichever of them is going away must not take the focus with it. Send is normally
        // covered by SubmitCurrentText claiming the draft first, but Stop is not: clicking Stop
        // leaves focus on the stop button, and the button disappears the moment the run settles.
        // Both then dump focus on the approval-mode drop-down. See SubmitCurrentText.
        ReturnFocusToDraftIfLeaving(SendButton, showSend);
        ReturnFocusToDraftIfLeaving(StopButton, showStopButton);

        SendButton.Visibility = showSend ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = canSend && !isActive;
        SendButton.Background = canSend && !isActive
            ? Brush("ComposerPrimaryButtonBackgroundBrush")
            : Brush("ComposerDisabledButtonBackgroundBrush");
        SendIcon.Foreground = canSend && !isActive
            ? Brush("ComposerPrimaryButtonForegroundBrush")
            : Brush("ComposerDisabledButtonForegroundBrush");

        var showStop = CanStop || IsStopping || _showStopFeedback;
        StopButton.Visibility = showStopButton ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = CanStop && !IsStopping && !_showStopFeedback && !isActive;
        // Match the familiar ChatGPT stop affordance: the square remains a stable target and the
        // ring rotates only while Stop is actionable. Clicking immediately freezes the motion,
        // which acknowledges the request even before the host settles the run.
        var animateStop = CanStop && !IsStopping && !_showStopFeedback && !isActive;
        StopGlyph.Visibility = showStop ? Visibility.Visible : Visibility.Collapsed;
        StopRing.IsActive = animateStop;
        StopRing.Visibility = animateStop ? Visibility.Visible : Visibility.Collapsed;
        var stopLabel = IsStopping || _showStopFeedback
            ? LocalizedStrings.Get("RuntimeComposerStopping", "Stopping...")
            : LocalizedStrings.Get("RuntimeComposerStop", "Stop");
        ToolTipService.SetToolTip(StopButton, stopLabel);
        AutomationProperties.SetName(StopButton, stopLabel);

        RefreshModeSelector();
        if (isActive) ModeButton.Visibility = Visibility.Collapsed;

        FooterMetadataPanel.Visibility = isActive ? Visibility.Collapsed : Visibility.Visible;

        SpeechIcon.Visibility = isActive ? Visibility.Collapsed : Visibility.Visible;
        SpeechRing.IsActive = isActive;
        SpeechRing.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;

        RecordingWaveformHost.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        CancelSpeechButton.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        CancelSpeechButton.IsEnabled = isActive;
        FinishSpeechButton.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
        FinishSpeechButton.IsEnabled = isRecording;
        SpeechButton.Visibility = isActive ? Visibility.Collapsed : Visibility.Visible;

        BufferSpinner.IsActive = isStarting || isTranscribing;
        BufferSpinner.Visibility = (isStarting || isTranscribing) ? Visibility.Visible : Visibility.Collapsed;
        WaveformCanvas.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
        WaveformTimerText.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
        VoiceStatusText.Visibility = (isStarting || isTranscribing)
            ? Visibility.Visible
            : Visibility.Collapsed;
        VoiceStatusText.Text = isTranscribing
            ? LocalizedStrings.Get("VoiceTranscribing", "Transcribing...")
            : LocalizedStrings.Get("VoiceStarting", "Starting microphone...");

        var voiceStatus = isRecording
            ? LocalizedStrings.Get(
                "VoiceRecordingInstructions",
                "Recording. Press Enter to finish or Escape to discard.")
            : VoiceStatusText.Text;
        AutomationProperties.SetName(RecordingWaveformHost, voiceStatus);

        var cancelLabel = isTranscribing
            ? LocalizedStrings.Get("VoiceCancelTranscription", "Cancel transcription (Esc)")
            : LocalizedStrings.Get(
                "VoiceCancelInput", "Cancel and discard voice input (Esc)");
        ToolTipService.SetToolTip(CancelSpeechButton, cancelLabel);
        AutomationProperties.SetName(CancelSpeechButton, cancelLabel);

        var finishLabel = LocalizedStrings.Get(
            "VoiceFinishRecording", "Finish recording and transcribe (Enter)");
        ToolTipService.SetToolTip(FinishSpeechButton, finishLabel);
        AutomationProperties.SetName(FinishSpeechButton, finishLabel);
    }

    private static Brush Brush(string resourceKey)
        => (Brush)Application.Current.Resources[resourceKey];

    private void Composer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var composerWidth = (double)Application.Current.Resources["ChatComposerWidth"];
        var availableWidth = Math.Max(0, e.NewSize.Width - 48);
        ComposerPanel.Width = Math.Min(composerWidth, availableWidth);

        var isNarrow = availableWidth < 430;
        ModeLabel.Visibility = isNarrow ? Visibility.Collapsed : Visibility.Visible;
        ModeButton.Padding = isNarrow ? new Thickness(10, 0, 10, 0) : new Thickness(9, 0, 9, 0);
        RecordingWaveformHost.MinWidth = isNarrow ? 48 : 72;
        ComposerFooter.ColumnSpacing = isNarrow ? 2 : 6;
        ApplySuggestionVisibility(e.NewSize.Width);
    }

}
