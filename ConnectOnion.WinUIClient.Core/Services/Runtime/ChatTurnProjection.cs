using System.Text;
using System.Text.Json;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>
/// The single source of truth for turning a run's ordered raw stream events into
/// <see cref="ChatMessage"/> bubbles. Both the live chat view model (rendering on the
/// UI thread) and the app-level persistence path (saving a finished turn when no page
/// exists) drive the exact same projection, so a bubble shows/persists identically no
/// matter who built it. Relocated from the former <c>ChatViewModel.ApplyStreamEvent</c>
/// so there is one mapping, not two.
///
/// An instance is stateful for the duration of one turn (it accumulates the per-turn
/// model/token/duration metrics) and is <b>not</b> thread-safe: the caller applies
/// events on a single thread (the UI dispatcher, or the persistence task).
/// </summary>
public sealed partial class ChatTurnProjection
{
    private readonly IChatProjectionTarget _target;

    /// <summary>Stand-in root for a frame whose JSON didn't parse. Deliberately never disposed —
    /// it is two bytes, shared for the process's lifetime.</summary>
    private static readonly JsonDocument EmptyObject = JsonDocument.Parse("{}");

    // Accumulated across every llm_call/llm_result iteration of the current turn.
    private string? _turnModel;
    private long _turnTokensIn;
    private long _turnTokensOut;
    private bool _turnHasUsage;
    private double _turnDurationMs;
    private double? _turnContextPercent;
    private long _turnToolCallsCount;
    private ToolActivityProjector? _toolActivity;
    private ChatMessage? _streamedFinalReply;
    private readonly List<ChatMessage> _thinkingActivities = new();
    // The card the current visible run of `thinking` frames is accumulating into. Protocol-only
    // lifecycle frames do not end the run: Add clears this only when another transcript row is
    // actually inserted between thoughts.
    private ChatMessage? _currentThinking;
    // The turn's single spinner row, opened by the first llm_call. Distinct from _currentThinking:
    // that one holds reasoning text the agent sent, this one holds nothing and exists only so a
    // long LLM call does not look like a hung app.
    //
    // One row for the whole turn, reused across iterations. An agentic turn makes an llm_call per
    // iteration — fifteen tools is eight or more round trips — and a row each left a column of
    // "Thought for 2.4 s / 6.6 s / 5.1 s …" that says nothing an aggregate does not.
    private ChatMessage? _llmThinking;
    // Set once this turn has produced a thinking card with real reasoning text. A turn-level
    // latch rather than a "_currentThinking is open" check, because ApplyParsed clears that field
    // on every non-thinking frame — including the llm_call we are deciding about.
    private bool _sawThinkingText;
    // Inbound agent images are normally buffered until the final output. The one exception is an
    // ask_user frame: agents use an immediately preceding image for QR sign-in and other visual
    // questions, so that image is attached to the live question before the host waits for input.
    private readonly List<PendingAgentImage> _pendingAgentImages = new();
    private sealed record PendingAgentImage(string Source, string? MimeType, bool IsCached);
    private bool _wasInterrupted;
    // plan_review has no protocol id. Keep the one card emitted by this turn so reconnect
    // replay cannot create a fresh actionable copy after the original was already answered.
    private readonly Dictionary<string, ChatMessage> _planReviewCards = new(StringComparer.Ordinal);
    // remote_write_file opens as a tool call, emits diff_preview + ask_user, then eventually
    // returns a tool_result. Keeping this turn-local correlation is what prevents an accepted
    // proposal from being labelled Applied before the tool itself confirms success.
    private readonly List<string> _writeCallsAwaitingDiff = new();
    private readonly Dictionary<string, ChatMessage> _diffsByToolId = new(StringComparer.Ordinal);

    public ChatTurnProjection(IChatProjectionTarget target) => _target = target;

    /// <summary>Tool calls the agent reported this turn (used for the completion notification).</summary>
    public long TurnToolCallsCount => _turnToolCallsCount;

    /// <summary>Total wall-clock the agent spent in LLM calls this turn (for the notification).</summary>
    public double TurnDurationMs => _turnDurationMs;

    /// <summary>
    /// Applies one stream event to the message list. Interactive turns
    /// (ask_user/approval/plan_review) and onboarding arrive here as synthetic events
    /// carrying their raw JSON (see <c>AgentTurnExecutor</c>) so a page re-created mid-turn
    /// reconstructs those bubbles too, not just the plain streaming ones.
    /// </summary>
    public void Apply(AgentStreamEvent e)
    {
        // The document is kept alive for the whole of Apply and disposed at the end, rather than
        // cloning its root out of a using-block: Clone() deep-copies the entire payload, which for
        // an llm_call frame is the conversation's whole message history and for an agent_image is
        // the base64 image — paid once per event, on both the live and the persist pass.
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(e.RawJson); } catch { /* leave root undefined below */ }

        try
        {
            ApplyParsed(e, doc?.RootElement ?? default);
        }
        finally
        {
            doc?.Dispose();
        }
    }

    /// <summary>
    /// Mirrors oo-chat's optimistic stop projection: running activity rows and tool steps stop
    /// animating immediately, while the underlying turn remains open for its closing events and
    /// final OUTPUT. Interactive cards are deliberately left actionable.
    /// </summary>
    public static void ApplyOptimisticStopVisuals(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.IsActivityEvent && message.Status == EventStatus.Running)
            {
                message.Status = EventStatus.Done;
                message.ThinkingElapsedLabel = "";
            }

            if (message.ToolActivity is { IsTerminal: false } activity)
                new ToolActivityProjector(activity).CompleteOptimistically();
        }
    }


    /// <summary>
    /// Adds the final agent reply exactly once — only if it differs from the last agent
    /// bubble the stream already produced (an <c>assistant</c> event), matching the SDK so
    /// the same text never renders twice.
    /// </summary>
    public void AppendFinalReply(string reply)
    {
        _toolActivity?.Complete();
        CompleteThinkingActivities(EventStatus.Done);
        if (string.IsNullOrEmpty(reply)) return;
        if (_streamedFinalReply is not null && _streamedFinalReply.Content == reply)
        {
            if (_wasInterrupted) _streamedFinalReply.EventMeta = "Stopped";
            return;
        }

        Add(new ChatMessage
        {
            Id = NextId(),
            Role = ChatRole.Agent,
            Content = reply,
            AgentName = AgentName,
            EventMeta = _wasInterrupted ? "Stopped" : null,
        });
    }

    /// <summary>
    /// Completes a successful turn in reading order: tool activity, the quiet consumption summary,
    /// then the final output. If the stream already emitted the final assistant bubble, it is moved
    /// to the end of the process records instead of being duplicated.
    ///
    /// <para><b>The summary goes above the answer, and the ordering is load-bearing twice over.</b>
    /// It is process metadata about the turn, so it belongs with the other process records rather
    /// than after the thing the user actually came to read — a turn should end on the answer, not
    /// on a token count. And because persisted history is replayed with <c>ORDER BY id</c>, the
    /// emission order here has to equal the id order, or a reopened conversation would show these
    /// two rows the other way round from the live view that produced them.</para>
    /// </summary>
    public void AppendCompletedTurn(string reply)
    {
        _toolActivity?.Complete();
        CompleteOpenDiffs(DiffChangeState.Unconfirmed);
        CompleteThinkingActivities(EventStatus.Done);

        // Reuse the streamed bubble only when it *is* the final answer. An empty reply counts as
        // a match (the OUTPUT carried nothing new), but a reply that differs means the stream's
        // bubble was a partial, so it stays where it is and the real answer is appended below.
        var streamedFinal = _streamedFinalReply is not null
            && (string.IsNullOrEmpty(reply) || _streamedFinalReply.Content == reply)
                ? _streamedFinalReply
                : null;
        // Lift the streamed bubble out so the summary can be emitted ahead of it.
        if (streamedFinal is not null) Messages.Remove(streamedFinal);

        AppendTurnSummary();

        if (streamedFinal is not null)
        {
            if (_wasInterrupted) streamedFinal.EventMeta = "Stopped";
            // Renumbered, not merely re-added. The bubble was created mid-turn so it carries an id
            // below the summary's, and leaving it there would make the persisted `ORDER BY id`
            // replay disagree with what the user just watched. Safe because `_streamedFinalReply`
            // is only ever a message this projection created (see the `assistant` arm in
            // ChatTurnProjection.Events) — never a row loaded from the database, so no already
            // persisted row is being re-keyed.
            streamedFinal.Id = NextId();
            Add(streamedFinal);
        }
        else
        {
            AppendFinalReply(reply);
        }

        // Images belong to the final assistant output, and FlushPendingImages hangs them on the
        // last agent bubble — so it runs after that output is back in place, which is now also the
        // end of the turn.
        FlushPendingImages();
    }

    /// <summary>
    /// What the turn has spent so far, for the live readout beside the composer.
    ///
    /// <para>The same accumulators the summary row is built from, published mid-turn instead of
    /// only at the end. It is the running total on purpose: a per-iteration figure would flicker
    /// and answer a question nobody asks, whereas "this turn has cost me 12k tokens and is at 40%
    /// of context" is exactly what a user watching a long turn wants to know <i>while</i> they can
    /// still do something about it.</para>
    /// </summary>
    public TurnUsage CurrentUsage => new(
        _turnHasUsage ? _turnTokensIn : null,
        _turnHasUsage ? _turnTokensOut : null,
        _turnContextPercent,
        _turnToolCallsCount);

    /// <summary>One quiet summary row immediately before the final output: model +
    /// duration/tokens/context/tools. See <see cref="AppendCompletedTurn"/> for why it sits
    /// above the answer rather than below it.</summary>
    public void AppendTurnSummary()
    {
        var parts = new List<string>();
        if (_turnDurationMs > 0) parts.Add(FormatDuration(_turnDurationMs));
        if (_turnHasUsage) parts.Add($"{FormatTokenCount(_turnTokensIn)}→{FormatTokenCount(_turnTokensOut)} tok");
        if (_turnContextPercent is { } ctx) parts.Add($"ctx {ctx:0.#}%");
        if (_turnToolCallsCount > 0) parts.Add($"{_turnToolCallsCount:0} tools");

        Add(new ChatMessage
        {
            Id = NextId(),
            Role = ChatRole.Event,
            EventKind = "activity",
            EventKey = "turn_usage",
            EventTitle = _turnModel ?? "Turn usage",
            // The bubble is added even when the agent reported nothing measurable. "Usage not
            // reported" is an honest answer; omitting the row entirely would leave the user
            // unable to tell a free turn from an unreported one.
            EventMeta = parts.Count > 0 ? string.Join(" · ", parts) : "Usage not reported",
            Status = EventStatus.Done,
        });
    }

    /// <summary>Finalizes the current compact timeline when the whole assistant turn fails
    /// or is cancelled. The error stays in the activity details rather than becoming a second
    /// oversized tool bubble.</summary>
    public void CompleteToolActivity(ToolActivityStatus status, string? error = null)
    {
        _toolActivity?.Complete(status, error);
        var diffState = status == ToolActivityStatus.Failed
            && (error?.Contains("connection", StringComparison.OrdinalIgnoreCase) == true
                || error?.Contains("socket", StringComparison.OrdinalIgnoreCase) == true
                || error?.Contains("websocket", StringComparison.OrdinalIgnoreCase) == true)
            ? DiffChangeState.Disconnected
            : status == ToolActivityStatus.Failed ? DiffChangeState.Failed : DiffChangeState.Unconfirmed;
        CompleteOpenDiffs(diffState);
        CompleteThinkingActivities(status == ToolActivityStatus.Failed
            ? EventStatus.Error
            : EventStatus.Done);
        // A turn that failed or was cancelled may still have received images before it stopped;
        // don't lose them — flush onto whatever output exists so far.
        FlushPendingImages();
    }

    /// <summary>
    /// Projects the images buffered from this turn's <c>agent_image</c> events, attaching them to
    /// the final agent bubble (the reply) if that is the last message, or to a fresh agent bubble
    /// otherwise — so a received image always lands after the turn's final output, never above it.
    /// </summary>
    private void FlushPendingImages()
    {
        if (_pendingAgentImages.Count == 0) return;

        var target = Messages.Count > 0 && Messages[^1].Role == ChatRole.Agent
            ? Messages[^1]
            : null;
        if (target is null)
        {
            target = new ChatMessage { Id = NextId(), Role = ChatRole.Agent, AgentName = AgentName };
            Add(target);
        }

        AttachPendingImages(target);
    }

    /// <summary>Consumes the turn's buffered images and attaches them to a specific bubble.</summary>
    private void AttachPendingImages(ChatMessage target)
    {
        if (_pendingAgentImages.Count == 0) return;

        foreach (var image in _pendingAgentImages)
        {
            // Attached in the Encoding state and handed to the target, which decodes/caches it
            // to disk off this path and then flips the status. The bubble therefore renders a
            // placeholder immediately instead of blocking the turn on image I/O — and the
            // base64 payload never lands on the ChatMessage, only the resulting cache path.
            // "image" is a placeholder name; the real one comes from the content hash.
            var attachment = new ChatAttachment
            {
                Kind = AttachmentKind.Image,
                FileName = "image",
                MimeType = image.MimeType,
                LocalCachePath = image.IsCached ? image.Source : null,
                Status = image.IsCached ? AttachmentStatus.Sent : AttachmentStatus.Encoding,
            };
            target.Attachments.Add(attachment);
            if (!image.IsCached) _target.ResolveAgentImage(image.Source, attachment);
        }
        _pendingAgentImages.Clear();
    }

    /// <summary>Stops every thinking spinner this turn opened. There is no "thinking finished"
    /// frame on the wire — the agent simply moves on — so the loader is retired by whatever
    /// comes next (a reply, a failure, the end of the turn). Only Running rows are touched, so
    /// an already-settled one keeps the status it was settled with.</summary>
    private void CompleteThinkingActivities(EventStatus status)
    {
        SettleLlmThinking();
        _currentThinking = null;
        foreach (var thinking in _thinkingActivities)
        {
            if (thinking.Status != EventStatus.Running) continue;
            thinking.Status = status;
            thinking.ThinkingElapsedLabel = "";
        }
    }

    /// <summary>
    /// Settles the "Thinking..." row an <c>llm_call</c> opened, stamping how long the call took.
    /// The duration is the row's whole reason to survive: <c>llm_call</c> carries only a model and
    /// an iteration number, so without it a finished row says nothing the transcript does not
    /// already show. With it, "Thought for 2.0 s" is the one fact about the pause the user just
    /// sat through.
    ///
    /// <paramref name="durationMs"/> is the agent's own measurement when <c>llm_result</c>
    /// supplies one; otherwise it is wall-clock since the row appeared, which also covers the run
    /// that fails or is stopped mid-call and never gets a result frame at all.
    /// </summary>
    /// <summary>Opens the spinner row for one <c>llm_call</c>, if one is not already up.</summary>
    private void StartLlmThinking()
    {
        if (_llmThinking is not null) return;
        _llmThinking = new ChatMessage
        {
            Id = NextId(),
            Role = ChatRole.Event,
            EventKind = "activity",
            EventTitle = "Thinking",
            Status = EventStatus.Running,
        };
        _thinkingActivities.Add(_llmThinking);
        Add(_llmThinking);
    }

    /// <summary>
    /// Takes the spinner row away once the call comes back. It is removed, not settled with a
    /// duration: the elapsed seconds are the live counter's job while the model is working, and
    /// once it has finished the pause is over and there is nothing left to report — the turn's
    /// total time is already on the usage line. Removing also means a turn with a dozen
    /// iterations never stacks up a dozen spent rows.
    ///
    /// Called on <c>llm_result</c>, on the first <c>thinking</c> frame (real reasoning text
    /// supersedes the bare indicator), and at turn end — a run that dies mid-call never gets its
    /// result frame and must not leave a spinner turning forever.
    /// </summary>
    private void SettleLlmThinking()
    {
        if (_llmThinking is null) return;
        Messages.Remove(_llmThinking);
        _thinkingActivities.Remove(_llmThinking);
        _llmThinking = null;
    }

    private void CompleteOpenDiffs(DiffChangeState state)
    {
        foreach (var diff in Messages.Where(message => message.IsDiffPreviewEvent
            && message.DiffState is DiffChangeState.Pending or DiffChangeState.Applying))
        {
            diff.SetDiffState(state);
        }

        // No blanket collapse here any more. SetDiffState already routes each card through
        // SynchronizeDiffPresentation, which applies the right default for the state it landed in
        // — and that default is now state-specific rather than uniform. Sweeping every diff shut at
        // turn end contradicted it in exactly the case that matters: a Failed, PartiallyApplied,
        // Disconnected or Unconfirmed diff is the one whose body and DiffProblemText the user needs
        // to read, and it was being folded away alongside the clean ones. It also overrode any
        // deliberate expansion the user had made while the turn ran.
    }

    private static bool IsWriteFileTool(string toolName)
    {
        var normalized = toolName.Replace('-', '_').ToLowerInvariant();
        return normalized.Contains("write_file", StringComparison.Ordinal)
            && !normalized.Contains("diff_preview", StringComparison.Ordinal);
    }

    /// <summary>The turn's one tool-activity card, created on first use. Lazily, because most
    /// turns call no tools at all and an empty card is pure clutter; one per turn, because the
    /// card's job is to collapse a turn's whole tool timeline into a single expandable row.</summary>
    private ToolActivityProjector GetToolActivity()
    {
        if (_toolActivity is not null) return _toolActivity;
        var activity = new ToolActivityViewModel
        {
            TurnId = Guid.NewGuid().ToString("N"),
            DisplayMode = ToolDisplayMode.Compact,
            // Starts collapsed (the model's default) — stated explicitly because this is the
            // card a live turn creates, and it is the one users see most.
            IsExpanded = false,
        };
        Add(new ChatMessage
        {
            Id = NextId(),
            Role = ChatRole.Event,
            EventKind = "tool_activity",
            EventTitle = "Tool execution",
            Status = EventStatus.Running,
            ToolActivity = activity,
        });
        _toolActivity = new ToolActivityProjector(activity, _target.IsLiveView);
        return _toolActivity;
    }

    // ---- helpers (relocated verbatim) ----

    private IList<ChatMessage> Messages => _target.Messages;
    private string? AgentName => _target.AgentName;
    private long NextId() => _target.NextId();
    private void Add(ChatMessage message)
    {
        // Group by what the transcript actually shows, not by invisible wire traffic. Events such
        // as llm_call/llm_result and tool_result mutate state without adding a row; clearing on
        // those produced two adjacent Thinking blocks with nothing visible between them.
        if (!ReferenceEquals(message, _currentThinking)) _currentThinking = null;
        _target.Add(message);
    }
    private void ReportUsage() => _target.ReportUsage(CurrentUsage);

    private ChatMessage NewActivity(string? key, string title, EventStatus status) => new()
    {
        Id = NextId(),
        Role = ChatRole.Event,
        EventKind = "activity",
        EventKey = key,
        EventTitle = title,
        Status = status,
    };

    /// <summary>Finds the open row a completion frame belongs to. <c>LastOrDefault</c>, not
    /// <c>FirstOrDefault</c>: agents reuse ids across a long conversation, and the most recent
    /// row carrying that key is always the one this frame is completing.</summary>
    private ChatMessage? FindEvent(string? key, string kind)
        => string.IsNullOrEmpty(key)
            ? null
            : Messages.LastOrDefault(m => m.IsEvent && m.EventKind == kind && m.EventKey == key);

    private static string FormatDuration(double ms)
        => ms < 1000 ? $"{ms:0} ms" : $"{ms / 1000:0.0} s";

    /// <summary>Re-indents a JSON blob for the approval card's argument pane. Unparseable input
    /// is returned untouched rather than swallowed — the user still needs to see what the agent
    /// is asking to run, even if it isn't valid JSON.</summary>
    internal static string? PrettyJson(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                doc.RootElement.WriteTo(writer);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return raw;
        }
    }

    private static (long? input, long? output) TryParseRawTokenUsage(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            long? input = u.TryGetProperty("input_tokens", out var i) && i.TryGetInt64(out var iv) ? iv : null;
            long? output = u.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var ov) ? ov : null;
            return (input, output);
        }
        return (null, null);
    }

    private static string FormatTokenCount(long tokens) =>
        tokens switch
        {
            >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
            >= 1_000 => $"{tokens / 1000.0:0.#}K",
            _ => tokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
}

/// <summary>
/// The surface a <see cref="ChatTurnProjection"/> writes into. Implemented over a live
/// <c>ObservableCollection&lt;ChatMessage&gt;</c> by the view model and over a plain list by
/// the persistence path, so both share one projection with different side-effect handling.
/// </summary>
/// <summary>
/// A turn's running spend, published while the turn is still going.
/// </summary>
/// <param name="TokensIn">Prompt tokens so far, or null when the agent has reported no usage at
/// all — which is not the same as zero, and the readout says so by showing nothing.</param>
/// <param name="TokensOut">Completion tokens so far, same null semantics.</param>
/// <param name="ContextPercent">How full the context window is, as last reported.</param>
/// <param name="ToolCalls">Tool calls the agent has made this turn.</param>
public readonly record struct TurnUsage(
    long? TokensIn,
    long? TokensOut,
    double? ContextPercent,
    long ToolCalls)
{
    /// <summary>Nothing measurable has been reported yet, so there is nothing to show. Keeps the
    /// composer from reserving space for a readout that may never arrive.</summary>
    public bool IsEmpty => TokensIn is null && TokensOut is null && ContextPercent is null && ToolCalls == 0;
}

public interface IChatProjectionTarget
{
    IList<ChatMessage> Messages { get; }
    string? AgentName { get; }

    /// <summary>
    /// True when this projection is driving a chat page the user is actually looking at, false
    /// when it is the headless persistence pass. The only thing it gates is auto-expansion of a
    /// failed tool card: springing a card open is a way of interrupting someone, which only makes
    /// sense if they are there to be interrupted. Persisted history opens collapsed regardless, so
    /// reopening an old conversation does not replay every past failure at full height.
    /// </summary>
    bool IsLiveView { get; }
    long NextId();
    void Add(ChatMessage message);

    /// <summary>Decode/cache an inbound agent image and fill in the attachment's local path.
    /// Live UI kicks this off-thread; persistence awaits it synchronously.</summary>
    void ResolveAgentImage(string dataUrl, ChatAttachment attachment);

    /// <summary>
    /// The turn's running spend changed. Live views show it beside the composer; the headless
    /// persistence pass has nobody to show it to and ignores it — the same figures reach storage
    /// through the summary row either way.
    /// </summary>
    void ReportUsage(TurnUsage usage);
}
