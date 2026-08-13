using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ConnectOnion.Protocol;
using ConnectOnion.Protocol.Runtime;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Attachments;
using ConnectOnion.WinUIClient.Services.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>
/// The app-level facade tying the per-conversation connection registry to the headless
/// <see cref="ConversationRunRegistry"/>. Owns the turn lifecycle end to end: it persists
/// the user message, starts the run, drives the reply on an app-owned socket, and — when
/// the reply lands — projects the buffered stream into bubbles and writes them
/// <b>before</b> the run is marked Completed. Pages only ever subscribe.
/// </summary>
public sealed class AgentSessionManager : IAgentSessionManager, IRunPersistence
{
    /// <summary>Every unhandled fault the run pipeline raises funnels here. A turn that dies this
    /// way leaves the chat looking merely stuck, so this is often the only trace of why.</summary>
    private static readonly Action<ILogger, Exception?> LogRunFailure =
        LoggerMessage.Define(LogLevel.Error, new EventId(1, "RunFailure"),
            "Conversation run pipeline raised an unhandled fault");
    private static readonly Action<ILogger, string, Exception?> LogOutgoingImageCacheFailure =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, "OutgoingImageCacheFailure"),
            "Could not persist outgoing image {FileName} in the content cache; using its source path");

    private readonly ILogger<AgentSessionManager> _logger;
    private readonly NotificationCoordinator? _notifications;
    private readonly HttpClient _http;
    private readonly ConversationRepository _conversations;
    private readonly UsageRepository _usage;
    private readonly AgentConnectionRegistry _connections;
    private readonly ConversationRunRegistry _runs;
    private readonly ConcurrentDictionary<string, AgentConfig> _agentsById = new();
    // The registry intentionally persists a reply before publishing its terminal state. Keep
    // that short transition observable so a page opened in the gap does not replay the same
    // stream events onto the history it just loaded from the database. Each marker is only
    // needed for that brief window, so the set is FIFO-capped (see MarkHistoryPersisted) rather
    // than grown one entry per turn for the whole app lifetime.
    private readonly ConcurrentDictionary<string, byte> _historyPersistedRuns = new();
    private readonly ConcurrentQueue<string> _historyPersistedOrder = new();
    private const int MaxPersistedRunMarkers = 128;

    // Interactive-turn answers (ask_user / approval / plan_review) captured from the page as the
    // user resolves them, keyed by conversation id and consumed at persist time in the order the
    // cards were created. The live stream carries each *request* but never the user's reply, so
    // without this the app-level persist (which re-projects cards from the reply-less event
    // stream) has no way to record the chosen result — a reloaded card would show dead controls.
    private readonly InteractiveAnswerBuffer _interactiveAnswers = new();

    // The interactive turn (if any) the agent is currently blocked on, per conversation. This is
    // lifecycle bookkeeping for interactive cards; the global Stop action always sends INTERRUPT.
    private readonly ConcurrentDictionary<string, string> _pendingInteractive = new();

    // Conversations whose connection already has its reconnect/divergence handlers attached.
    // GetOrCreate hands back the same long-lived service on every send, so without this guard
    // each turn would add another copy of every handler and the UI would show one "Reconnecting"
    // row per turn the conversation had ever run.
    private readonly ConcurrentDictionary<string, byte> _wiredConnections = new();

    // Run id for each conversation whose graceful-stop frame has been sent. This is app-level
    // state so switching pages cannot re-enable Stop and send a duplicate frame.
    private readonly ConcurrentDictionary<string, string> _interruptRequestedRuns = new();

    /// <summary>
    /// Recorded for an ask_user/plan_review turn that ended without an answer. "Skipped", not
    /// "No selection": the turn moved on without the user's input, which is what skipping is —
    /// and it names an outcome rather than an absence, so reloaded history reads as something
    /// that happened instead of something missing.
    /// </summary>
    private const string SkippedMeta = "Skipped";

    /// <summary>
    /// An unanswered <b>approval</b> is recorded as a rejection, not as "no selection". The agent
    /// only ever proceeds on an explicit yes, so a turn that ended without one is a turn where the
    /// tool did not run — "Rejected" is what actually happened, and it is the safe reading to
    /// leave in history besides.
    /// </summary>
    private const string UnansweredApprovalMeta = "Rejected";

    /// <param name="usage">Optional so existing callers and tests that don't care about accounting
    /// can omit it; a default instance is used when it isn't supplied.</param>
    /// <param name="notifications">Optional for the same reason, but it is what keeps this type on
    /// the testable side of the Core seam. Turn-completed and approval-required toasts are an
    /// app-level concern, and this used to reach them through the static <c>AppServices</c>
    /// locator — a reverse dependency on the app project that no headless test host can load, so
    /// the whole run runtime sat outside the coverage gate for the sake of three call sites. The
    /// coordinator itself already lives in Core; only the lookup did not. Null means "nothing is
    /// listening", which is exactly a headless test's situation, not a degraded mode.</param>
    public AgentSessionManager(
        HttpClient http,
        ConversationRepository conversations,
        UsageRepository? usage = null,
        ILogger<AgentSessionManager>? logger = null,
        NotificationCoordinator? notifications = null)
    {
        _http = http;
        _conversations = conversations;
        _usage = usage ?? new UsageRepository();
        _logger = logger ?? NullLogger<AgentSessionManager>.Instance;
        _notifications = notifications;
        _connections = new AgentConnectionRegistry(http);
        _runs = new ConversationRunRegistry(this, onError: ex =>
        {
            LogRunFailure(_logger, ex);
        });
    }

    /// <summary>The app-owned sockets, exposed so the view model can answer interactive turns
    /// and trigger explicit reconnects on the same connection the run uses.</summary>
    public AgentConnectionRegistry Connections => _connections;

    /// <summary>Records the result the user chose for an interactive turn (ask_user / approval /
    /// plan_review) so the app-level persist can stamp it onto the card it re-projects from the
    /// reply-less event stream. Called by the chat page the moment it resolves the turn; answers
    /// are consumed in creation order when the run is persisted.</summary>
    public void RecordInteractiveAnswer(string conversationId, string meta, EventStatus status)
        => _interactiveAnswers.RecordConfirmed(conversationId, meta, status);

    public Guid BeginInteractiveAnswer(string conversationId, string meta, EventStatus status)
        => _interactiveAnswers.Begin(conversationId, meta, status);

    public void ConfirmInteractiveAnswer(Guid reservationId)
        => _interactiveAnswers.Confirm(reservationId);

    public void CancelInteractiveAnswer(Guid reservationId)
        => _interactiveAnswers.Cancel(reservationId);

    // ---- IAgentSessionManager ----

    public async Task<SendMessageResult> SendMessageAsync(
        AgentConfig agent, string conversationId, string prompt,
        IReadOnlyList<PendingAttachment>? attachments = null,
        string? mode = null)
    {
        _agentsById[agent.Id] = agent;
        // A fresh run starts clean: drop any interactive answers left over from an abandoned
        // prior turn so they can't leak onto this run's cards at persist time.
        _interactiveAnswers.Reset(conversationId);
        _pendingInteractive.TryRemove(conversationId, out _);
        _interruptRequestedRuns.TryRemove(conversationId, out _);
        _wiredConnections.TryRemove(conversationId, out _);

        var prepared = await PrepareAndPersistUserInputAsync(
            conversationId, prompt, attachments).ConfigureAwait(false);

        var runId = Guid.NewGuid().ToString();
        var request = new TurnRequest(
            RunId: runId,
            ConversationId: conversationId,
            AgentId: agent.Id,
            UserMessageId: Guid.NewGuid().ToString(),
            AssistantMessageId: Guid.NewGuid().ToString(),
            Prompt: prompt,
            SessionId: conversationId,
            Mode: mode,
            AttachmentSources: prepared.Sources);

        await _conversations.InsertExecutionAsync(runId, conversationId, null, prompt, "running").ConfigureAwait(false);

        var connection = _connections.GetOrCreate(conversationId, agent);
        AttachConnectionHandlers(conversationId, agent, connection);
        _runs.StartRun(request, NewExecutor(agent, conversationId, connection));

        return new SendMessageResult(
            runId, request.UserMessageId, request.AssistantMessageId, prepared.Message.Id);
    }

    public async Task SendRuntimeInputAsync(
        AgentConfig agent,
        string conversationId,
        string prompt,
        IReadOnlyList<PendingAttachment>? attachments = null)
    {
        var run = _runs.GetActiveRun(conversationId);
        if (run is null || run.IsTerminal)
            throw new InvalidOperationException("Cannot send runtime input: no agent turn is active.");

        var connection = _connections.Get(conversationId);
        if (connection is not { IsConnected: true })
            throw new InvalidOperationException("Cannot send runtime input: the agent connection is not open.");

        _agentsById[agent.Id] = agent;
        var prepared = await PrepareAndPersistUserInputAsync(
            conversationId, prompt, attachments).ConfigureAwait(false);
        var encoded = await EncodeAttachmentSourcesAsync(prepared.Sources).ConfigureAwait(false);
        await connection.SendRuntimeInputAsync(prompt, encoded.Images, encoded.Files).ConfigureAwait(false);
    }

    private sealed record PreparedUserInput(
        ChatMessage Message,
        IReadOnlyList<TurnAttachmentSource>? Sources);

    /// <summary>Builds the wire attachments and appends the matching user bubble. Fresh turns and
    /// runtime interjections share this path so their transcript/cache behavior cannot drift.</summary>
    private async Task<PreparedUserInput> PrepareAndPersistUserInputAsync(
        string conversationId,
        string prompt,
        IReadOnlyList<PendingAttachment>? attachments)
    {
        List<TurnAttachmentSource>? sources = null;
        var userAttachments = new List<ChatAttachment>();

        if (attachments is { Count: > 0 })
        {
            sources = new List<TurnAttachmentSource>();
            foreach (var attachment in attachments)
            {
                var localCachePath = attachment.LocalPath;
                if (attachment.Kind == AttachmentKind.Image)
                {
                    try
                    {
                        var cached = await ImageContentStore
                            .StoreFileAsync(attachment.LocalPath, attachment.MimeType)
                            .ConfigureAwait(false);
                        if (cached is not null)
                        {
                            localCachePath = cached;
                            // The page's optimistic bubble and a cancelled-send draft both reuse
                            // this object, so point them at the durable copy as well.
                            attachment.LocalPath = cached;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogOutgoingImageCacheFailure(_logger, attachment.FileName, ex);
                    }
                }

                userAttachments.Add(new ChatAttachment
                {
                    Kind = attachment.Kind,
                    FileName = attachment.FileName,
                    MimeType = attachment.MimeType,
                    SizeBytes = attachment.SizeBytes,
                    LocalCachePath = localCachePath,
                    Status = AttachmentStatus.Sent,
                });

                sources.Add(new TurnAttachmentSource(
                    attachment.FileName,
                    localCachePath,
                    attachment.MimeType,
                    attachment.Kind == AttachmentKind.Image));

                // Release the base64 payload immediately — the wire lists now hold it,
                // and it must not linger on the composer's draft.
            }
            if (sources.Count == 0) sources = null;
        }

        // Persist the user message up front so a page opening this conversation mid-turn
        // loads it from the DB and then replays the run's events on top. This is a pure append —
        // reading the whole conversation back just to take a max(id) and then rewriting every row
        // of it was two full table scans per turn for one new bubble.
        var userMessage = new ChatMessage
        {
            Id = await _conversations.GetNextMessageIdAsync(conversationId).ConfigureAwait(false),
            Role = ChatRole.User,
            Content = prompt,
        };
        foreach (var attachment in userAttachments) userMessage.Attachments.Add(attachment);
        await _conversations.UpsertMessagesAsync(
            conversationId, new[] { userMessage }).ConfigureAwait(false);

        return new PreparedUserInput(userMessage, sources);
    }

    internal static async Task<(IReadOnlyList<string>? Images, IReadOnlyList<OutgoingFileAttachment>? Files)>
        EncodeAttachmentSourcesAsync(IReadOnlyList<TurnAttachmentSource>? sources)
    {
        if (sources is not { Count: > 0 }) return (null, null);
        List<string>? images = null;
        List<OutgoingFileAttachment>? files = null;
        foreach (var source in sources)
        {
            var dataUrl = await AttachmentEncoder
                .EncodeToDataUrlAsync(source.LocalPath, source.MimeType)
                .ConfigureAwait(false);
            if (source.IsImage)
                (images ??= new List<string>()).Add(dataUrl);
            else
                (files ??= new List<OutgoingFileAttachment>()).Add(
                    new OutgoingFileAttachment(source.Name, dataUrl));
        }
        return (images, files);
    }

    /// <summary>
    /// Backs a send out completely: cancels the run, then erases the user bubble and the execution
    /// row that <see cref="SendMessageAsync"/> wrote before it started.
    ///
    /// <para><b>Only safe before the INPUT frame goes out.</b> The run reaches
    /// <see cref="ConversationRunStatus.Running"/> from the connection's <c>InputSent</c> event, so
    /// <c>Running</c> means the agent already has the message — erasing it then would leave the
    /// local transcript disagreeing with the session the host is still executing. The caller is
    /// responsible for that check; this method re-checks it and refuses, because the window is
    /// narrow and a caller racing it would otherwise delete a live turn's prompt.</para>
    ///
    /// Returns false if the run had already started sending, in which case nothing was touched.
    /// </summary>
    public async Task<bool> CancelSendAsync(string conversationId, string runId, long persistedMessageId)
    {
        var run = _runs.GetActiveRun(conversationId);
        if (run is null || run.RunId != runId) return false;
        if (run.Status is not (ConversationRunStatus.Queued or ConversationRunStatus.Connecting)) return false;

        await _runs.CancelRunAsync(conversationId, runId).ConfigureAwait(false);

        _interactiveAnswers.Reset(conversationId);
        _pendingInteractive.TryRemove(conversationId, out _);
        _interruptRequestedRuns.TryRemove(conversationId, out _);

        await _conversations.DeleteMessageAsync(conversationId, persistedMessageId).ConfigureAwait(false);
        await _conversations.DeleteExecutionAsync(runId).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Rejoins a turn this conversation started before the app was last closed, if the host is
    /// still running it. Called when a conversation is opened; a no-op for a settled one.
    ///
    /// <para><b>Why this exists.</b> Everything a turn renders — agent bubbles, tool cards, a
    /// pending approval — is written to SQLite in one batch when the turn reaches a terminal
    /// state. A process killed mid-turn therefore leaves only the user's message and an
    /// <c>executions</c> row still marked <c>running</c>, while the host may well still have an
    /// agent blocked on <c>io.receive()</c> waiting for that approval. Without this probe the
    /// conversation reopens looking merely truncated and the agent waits forever.</para>
    ///
    /// <para>Resuming re-runs the turn through the ordinary run pipeline, so the host's replayed
    /// events project into the usual cards and the usual terminal persist writes the whole turn
    /// to SQLite — including the approval card, now answerable again.</para>
    ///
    /// <para>Returns the resumed run, or null if there was nothing to resume. A conversation
    /// whose agent is gone has its stale execution row settled here so the probe does not fire
    /// again on every subsequent open.</para>
    /// </summary>
    public async Task<ConversationRunSnapshot?> TryResumeUnfinishedTurnAsync(
        string conversationId, AgentConfig agent)
    {
        // An active run already owns this conversation — either a live turn or a resume that a
        // previous open already started. Probing again would race a second socket onto it.
        if (_runs.IsRunActive(conversationId)) return null;

        var unfinished = await _conversations.GetUnfinishedExecutionAsync(conversationId).ConfigureAwait(false);
        if (unfinished is not { } execution) return null;

        _agentsById[agent.Id] = agent;
        var connection = _connections.GetOrCreate(conversationId, agent);
        AttachConnectionHandlers(conversationId, agent, connection);

        // Reuse the original run id so the resumed turn finalizes the execution row it belongs
        // to rather than opening a second one beside it.
        var request = new TurnRequest(
            RunId: execution.ExecutionId,
            ConversationId: conversationId,
            AgentId: agent.Id,
            UserMessageId: Guid.NewGuid().ToString(),
            AssistantMessageId: Guid.NewGuid().ToString(),
            Prompt: execution.Prompt,
            SessionId: conversationId);

        try
        {
            return _runs.StartRun(
                request,
                new AgentTurnExecutor(
                    connection,
                    r => NotifyApproval(agent, conversationId, r),
                    kind => _pendingInteractive[conversationId] = kind,
                    PrepareStreamEventAsync,
                    resumeExisting: true));
        }
        catch (InvalidOperationException)
        {
            // StartRun's one-run-per-conversation claim lost a race with a send. Harmless.
            return null;
        }
    }

    /// <summary>
    /// Recovers a turn the host <i>finished</i> while the app was closed.
    ///
    /// <para>The resume probe cannot rejoin such a turn — the host reports the session idle
    /// rather than running, which is what raised <see cref="SessionNotRunningException"/> — but
    /// the reply is not lost: the same CONNECT came back with <c>server_newer</c> and the host's
    /// whole session flattened into <c>chat_items</c>. Without this the reply is dropped and the
    /// conversation reopens looking like the agent never answered, which is the common case of
    /// closing the app on a turn that then completes a few seconds later.</para>
    ///
    /// <para>Two guards keep the append safe. The tail is anchored on the user message the
    /// <c>executions</c> row remembers (see
    /// <see cref="ChatItemsProjection.ParseTailAfterPrompt"/>), and the local transcript must
    /// still <i>end</i> at that exact message — proving nothing of the turn was persisted and
    /// that appending duplicates nothing. Either guard failing means we leave history alone.</para>
    ///
    /// <para>Returns whether anything was recovered.</para>
    /// </summary>
    private async Task<bool> TryRecoverFinishedTurnAsync(ConversationRunSnapshot snapshot)
    {
        try
        {
            var conversationId = snapshot.ConversationId;
            var chatItemsJson = _connections.Get(conversationId)?.LastConnectedState?.ChatItemsJson;
            if (string.IsNullOrEmpty(chatItemsJson)) return false;

            // Still 'running' at this point — this method runs before the row is settled.
            var unfinished = await _conversations.GetUnfinishedExecutionAsync(conversationId)
                .ConfigureAwait(false);
            if (unfinished is not { } execution) return false;

            var last = await _conversations.LoadLastMessageAsync(conversationId).ConfigureAwait(false);
            if (last is null
                || last.Role != ChatRole.User
                || !string.Equals(last.Content, execution.Prompt, StringComparison.Ordinal))
            {
                // The transcript does not end where the turn began, so we cannot tell what is
                // already there. Leaving a reply out beats writing it twice.
                LogRecoverSkipped(_logger, conversationId, null);
                return false;
            }

            var agentName = _agentsById.TryGetValue(snapshot.AgentId, out var a) ? a.Name : null;
            var nextId = await _conversations.GetNextMessageIdAsync(conversationId).ConfigureAwait(false);
            var tail = ChatItemsProjection.ParseTailAfterPrompt(
                chatItemsJson, execution.Prompt, agentName, nextId);
            if (tail.Count == 0) return false;

            await _conversations.UpsertMessagesAsync(conversationId, tail).ConfigureAwait(false);
            LogRecoveredTurn(_logger, tail.Count, conversationId, null);
            return true;
        }
        catch (Exception ex)
        {
            // Recovery is best-effort; never let it break the run pipeline.
            LogRunFailure(_logger, ex);
            return false;
        }
    }

    private static readonly Action<ILogger, string, Exception?> LogRecoverSkipped =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(4, "RecoverSkipped"),
            "Conversation {ConversationId} does not end at the unfinished turn's prompt; left history alone");

    private static readonly Action<ILogger, int, string, Exception?> LogRecoveredTurn =
        LoggerMessage.Define<int, string>(LogLevel.Information, new EventId(5, "RecoveredTurn"),
            "Recovered {Count} items for a turn the host finished while closed, in conversation {ConversationId}");

    /// <summary>
    /// Attaches the per-connection handlers that outlive a single turn: reconnect progress and
    /// the host's session-divergence signal. Idempotent — the registry hands back the same
    /// service for every turn in a conversation, so this must attach exactly once.
    /// </summary>
    private void AttachConnectionHandlers(
        string conversationId, AgentConfig agent, AgentConnectionService connection)
    {
        if (!_wiredConnections.TryAdd(conversationId, 0)) return;

        // Reconnect progress is published into the run's event buffer rather than raised as its
        // own UI concept, so it flows through the same path as every other event: it shows up
        // as an activity row in the transcript, it is replayed to a page that attaches
        // mid-outage, and it needs no extra plumbing per page.
        connection.Reconnecting += e => _runs.PublishLocalEvent(conversationId, new AgentStreamEvent(
            "reconnecting",
            $"Reconnecting (attempt {e.Attempt}/{ReconnectPolicy.MaxAttempts})",
            null,
            WireJson.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "reconnecting",
                ["attempt"] = e.Attempt,
                ["max_attempts"] = ReconnectPolicy.MaxAttempts,
                ["reason"] = e.Error.Message,
            })));

        connection.Reconnected += state => _runs.PublishLocalEvent(conversationId, new AgentStreamEvent(
            "reconnected",
            "Reconnected",
            null,
            WireJson.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "reconnected",
                ["status"] = state.Status,
                // The distinction the user cares about: "running" means their turn survived and
                // the missed events are being replayed, anything else means it did not.
                ["resumed"] = state.IsRunning,
            })));

        connection.SessionDiverged += state => _ = OnSessionDivergedAsync(conversationId, agent, state);
    }

    /// <summary>
    /// Handles a <c>server_newer</c> CONNECTED: the host's copy of this conversation was ahead
    /// of ours and it handed back the authoritative one.
    ///
    /// <para><b>The import only runs when we have nothing of our own.</b> The host's
    /// <c>chat_items</c> is a flattened projection of its session — positional ids, no
    /// attachments, no cached image paths, no resolved interactive outcomes, no tool-card state
    /// (see <see cref="ChatItemsProjection"/>). Adopting it over a populated conversation would
    /// trade richer local history for poorer remote history and renumber every row, breaking the
    /// <c>(conversation_id, id)</c> key the repository allocates from <c>MAX(id)+1</c>. So an
    /// empty local conversation — a fresh install, a reset database, a second device — takes the
    /// server's history, and a populated one keeps its own and logs the divergence.</para>
    /// </summary>
    private async Task OnSessionDivergedAsync(
        string conversationId, AgentConfig agent, ConnectedState state)
    {
        try
        {
            var existing = await _conversations.GetNextMessageIdAsync(conversationId).ConfigureAwait(false);
            if (existing > 1)
            {
                LogSessionDivergedKept(_logger, conversationId, null);
                return;
            }

            var imported = ChatItemsProjection.Parse(state.ChatItemsJson, agent.Name);
            if (imported.Count == 0) return;

            await _conversations.UpsertMessagesAsync(conversationId, imported).ConfigureAwait(false);
            LogSessionDivergedImported(_logger, imported.Count, conversationId, null);
        }
        catch (Exception ex)
        {
            // Never let a reconciliation failure take down the connection that triggered it.
            LogRunFailure(_logger, ex);
        }
    }

    private static readonly Action<ILogger, string, Exception?> LogSessionDivergedKept =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2, "SessionDivergedKept"),
            "Host reported a newer session for conversation {ConversationId}; kept richer local history");

    private static readonly Action<ILogger, int, string, Exception?> LogSessionDivergedImported =
        LoggerMessage.Define<int, string>(LogLevel.Information, new EventId(3, "SessionDivergedImported"),
            "Imported {Count} chat items from the host into empty conversation {ConversationId}");

    private AgentTurnExecutor NewExecutor(
        AgentConfig agent, string conversationId, AgentConnectionService connection)
        => new(
            connection,
            r => NotifyApproval(agent, conversationId, r),
            kind => _pendingInteractive[conversationId] = kind,
            PrepareStreamEventAsync);

    private async Task<AgentStreamEvent> PrepareStreamEventAsync(AgentStreamEvent streamEvent)
    {
        if (!string.Equals(streamEvent.Type, "agent_image", StringComparison.Ordinal))
            return streamEvent;

        try
        {
            using var document = JsonDocument.Parse(streamEvent.RawJson);
            if (!AttachmentWireEvents.TryGetAgentImageDataUrl(
                    WireMessage.Wrap(document.RootElement), out var dataUrl))
            {
                return streamEvent;
            }

            var resolved = await AttachmentImageCacheService
                .ResolveAndCacheAsync(dataUrl, _http)
                .ConfigureAwait(false);
            if (resolved is not { } image) return streamEvent;

            return streamEvent with
            {
                RawJson = WireJson.Serialize(new Dictionary<string, object?>
                {
                    ["type"] = streamEvent.Type,
                    ["id"] = streamEvent.EventId,
                    ["ts"] = streamEvent.Timestamp,
                    ["cached_path"] = image.LocalPath,
                    ["mime_type"] = image.MimeType,
                }),
            };
        }
        catch
        {
            // Image projection is best effort. Preserve the original event so the existing live
            // and persistence targets can still attempt their normal fallback path.
            return streamEvent;
        }
    }

    public ConversationRunSnapshot? GetActiveRun(string conversationId) => _runs.GetActiveRun(conversationId);

    public bool IsRunHistoryPersisted(string runId) => _historyPersistedRuns.ContainsKey(runId);

    public IReadOnlyList<ConversationRunSnapshot> GetActiveRuns() => _runs.GetActiveRuns();

    public IDisposable Subscribe(string conversationId, Action<ConversationRunSnapshot> handler)
        => _runs.Subscribe(conversationId, handler);

    public Task CancelRunAsync(string conversationId, string runId) => _runs.CancelRunAsync(conversationId, runId);

    public ConversationRunSnapshot? RetryRun(AgentConfig agent, string conversationId)
    {
        _agentsById[agent.Id] = agent;
        _interactiveAnswers.Reset(conversationId);
        _pendingInteractive.TryRemove(conversationId, out _);
        _interruptRequestedRuns.TryRemove(conversationId, out _);
        var connection = _connections.GetOrCreate(conversationId, agent);
        AttachConnectionHandlers(conversationId, agent, connection);
        return _runs.RetryRun(conversationId, NewExecutor(agent, conversationId, connection));
    }

    // ---- Graceful stop ----

    public void ClearPendingInteractive(string conversationId)
        => _pendingInteractive.TryRemove(conversationId, out _);

    public async Task<StopOutcome> RequestStopAsync(string conversationId)
    {
        var run = _runs.GetActiveRun(conversationId);
        if (run is null || run.IsTerminal) return StopOutcome.NotRunning;

        var connection = _connections.Get(conversationId);
        if (connection is not { IsConnected: true }) return StopOutcome.NotRunning;

        // Only the first request needs a local state marker, but every invocation sends an
        // INTERRUPT frame. The host treats duplicates as idempotent and drains them.
        if (_interruptRequestedRuns.TryAdd(conversationId, run.RunId))
        {
            _runs.PublishLocalEvent(conversationId, new AgentStreamEvent(
                "interrupt_requested", "Stopping", null,
                "{\"type\":\"interrupt_requested\"}"));
        }

        try
        {
            await connection.SendInterruptAsync().ConfigureAwait(false);
            return StopOutcome.StopRequested;
        }
        catch
        {
            _interruptRequestedRuns.TryRemove(conversationId, out _);
            _runs.PublishLocalEvent(conversationId, new AgentStreamEvent(
                "interrupt_request_failed", "Stop failed", null,
                "{\"type\":\"interrupt_request_failed\"}"));
            throw;
        }
    }

    // ---- Mode ----

    public Task SetModeAsync(string conversationId, string mode)
    {
        var connection = _connections.Get(conversationId);
        if (connection is null) return Task.CompletedTask;

        // Remembered for the next CONNECT this socket makes (only a brand-new session keeps it —
        // the host's merge discards ours otherwise), and pushed now if a turn is live to receive
        // it. With no turn running the host would drop a mode_change on the floor, so we don't
        // send one: SendMessageAsync carries the mode with the INPUT instead.
        connection.InitialMode = mode;

        var run = _runs.GetActiveRun(conversationId);
        return run is { IsTerminal: false } ? connection.SetModeAsync(mode) : Task.CompletedTask;
    }

    private void NotifyApproval(AgentConfig agent, string conversationId, ApprovalRequest r)
        => _notifications?.PublishApprovalRequired(
            agent.Id, agent.Name, conversationId, r.Tool, r.Description, r.ArgumentsJson);

    /// <summary>Releases everything tied to a deleted conversation: cancels its active run (if
    /// any), closes and drops its app-owned socket, and clears its pending interactive answers.
    /// Called when the user deletes a session or agent so a removed conversation leaves no live
    /// WebSocket or in-memory state behind.</summary>
    public async Task ReleaseConversationAsync(string conversationId)
    {
        var run = _runs.GetActiveRun(conversationId);
        if (run is not null)
        {
            try { await _runs.CancelRunAsync(conversationId, run.RunId).ConfigureAwait(false); }
            catch { /* best-effort: the socket is torn down next regardless */ }
        }
        _interactiveAnswers.Reset(conversationId);
        _pendingInteractive.TryRemove(conversationId, out _);
        _interruptRequestedRuns.TryRemove(conversationId, out _);
        // After the cancel above, so a run unwinding into its terminal snapshot cannot re-add the
        // entry we are dropping. The registry's own maps are keyed by conversation too, and a
        // deleted conversation must not leave its last reply sitting in one of them.
        _runs.Forget(conversationId);
        await _connections.RemoveAsync(conversationId).ConfigureAwait(false);
    }

    /// <summary>Forgets the small agent-config lookup after all of that agent's conversations
    /// have been released. The dictionary is app-scoped and otherwise retains deleted agents.</summary>
    public void ForgetAgent(string agentId) => _agentsById.TryRemove(agentId, out _);

    /// <summary>Records that a run's history is on disk, bounded FIFO so the marker set never
    /// grows past <see cref="MaxPersistedRunMarkers"/> — each entry only matters for the brief
    /// window between the conversation write and the terminal snapshot.</summary>
    private void MarkHistoryPersisted(string runId)
    {
        if (!_historyPersistedRuns.TryAdd(runId, 0)) return;
        _historyPersistedOrder.Enqueue(runId);
        while (_historyPersistedOrder.Count > MaxPersistedRunMarkers
            && _historyPersistedOrder.TryDequeue(out var old))
        {
            _historyPersistedRuns.TryRemove(old, out _);
        }
    }

    /// <summary>Real app exit: stop new runs, cancel in-flight ones within a bounded window,
    /// then release the sockets. Never blocks forever.</summary>
    public async Task ShutdownAsync()
    {
        await _runs.ShutdownAsync(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
        await _connections.DisposeAllAsync().ConfigureAwait(false);
    }

    // ---- IRunPersistence (invoked by the registry off the UI thread) ----

    public async Task PersistCompletedAsync(ConversationRunSnapshot snapshot, string finalReply)
    {
        var agentName = _agentsById.TryGetValue(snapshot.AgentId, out var a) ? a.Name : null;

        _pendingInteractive.TryRemove(snapshot.ConversationId, out _);
        _interruptRequestedRuns.TryRemove(snapshot.ConversationId, out _);

        var (window, nextId) = await LoadTurnWindowAsync(snapshot.ConversationId).ConfigureAwait(false);
        var target = new ListProjectionTarget(window, agentName, _http, nextId);
        var projection = new ChatTurnProjection(target);
        foreach (var e in snapshot.Events) projection.Apply(e);
        projection.AppendCompletedTurn(finalReply);
        if (target.PendingImages.Count > 0)
        {
            try { await Task.WhenAll(target.PendingImages).ConfigureAwait(false); }
            catch { /* a single bad image must not fail the whole persist */ }
        }

        await ResolveInteractiveCardsAsync(snapshot.ConversationId, window).ConfigureAwait(false);

        await _conversations.UpsertMessagesAsync(
            snapshot.ConversationId, window.Where(m => !m.IsOnboarding).ToList()).ConfigureAwait(false);

        // Set this immediately after the conversation write (rather than after trace/execution
        // bookkeeping) because a newly created page can subscribe during that interval.
        MarkHistoryPersisted(snapshot.RunId);

        await PersistTraceAsync(snapshot).ConfigureAwait(false);
        await PersistUsageAsync(snapshot, agentName).ConfigureAwait(false);
        await _conversations.FinalizeExecutionAsync(
            snapshot.RunId, snapshot.ConversationId, finalReply, "done", projection.TurnDurationMs).ConfigureAwait(false);

        // A completed turn is a natural point to release sockets for other conversations that
        // have gone idle, so a long session never accumulates live WebSockets without bound.
        // IsRunActive, not GetActiveRun: the latter falls back to the last terminal snapshot, so
        // it reports every conversation that has ever run as busy and trimmed nothing at all.
        await _connections.TrimIdleAsync(_runs.IsRunActive).ConfigureAwait(false);

        // Completion notification is now an app-level concern (fires even in the tray with no
        // page open); the coordinator suppresses it when a window already shows this chat.
        if (agentName is not null)
        {
            _notifications?.NotifyConnectionRestored(snapshot.AgentId);
            _notifications?.PublishAgentTurnCompleted(
                snapshot.AgentId, agentName, snapshot.ConversationId, snapshot.RunId,
                finalReply, projection.TurnToolCallsCount, projection.TurnDurationMs);
        }
    }

    /// <summary>
    /// A resume probe that found no agent still running the turn. Not a failure in any sense the
    /// user should see: the socket was fine, the turn simply ended (or died on the host) while
    /// the app was closed. <see cref="ConversationRunRegistry"/> stamps the exception's type name
    /// as the run's error code, which is what makes this recognizable downstream.
    /// </summary>
    internal static bool IsAbandonedResume(ConversationRunSnapshot snapshot)
        => snapshot.ErrorCode == nameof(SessionNotRunningException);

    /// <summary>
    /// The app closed on top of a running turn. The host was never told to stop, so its agent is
    /// probably still executing this turn — which makes it a candidate for the resume probe on
    /// the next open, not a finished turn to write down.
    /// </summary>
    internal static bool IsShutdownInterrupted(ConversationRunSnapshot snapshot)
        => snapshot.ErrorCode == RunErrorCodes.Shutdown;

    public async Task PersistFailedAsync(ConversationRunSnapshot snapshot)
    {
        // Persist nothing and settle nothing: leaving the executions row at 'running' is exactly
        // what makes the next open probe the host and rejoin the turn.
        //
        // Writing the partial turn here would actively break that. The resumed turn is replayed
        // from the *start* of the host's buffer (a restarted client has no last_msg_id, so
        // rewind_to(null) replays everything), so any bubble written now would be projected a
        // second time on resume and the conversation would show the turn twice. Worse,
        // ResolveInteractiveCards would seal the still-pending approval as "No selection",
        // turning the one card the user came back to answer into dead history.
        //
        // The cost is that a turn nobody ever resumes loses its partial output and its token
        // usage — it was never persisted mid-turn anyway, and the abandoned path settles the row.
        if (IsShutdownInterrupted(snapshot))
        {
            _pendingInteractive.TryRemove(snapshot.ConversationId, out _);
            _interruptRequestedRuns.TryRemove(snapshot.ConversationId, out _);
            return;
        }

        // Nothing was replayed and nothing is coming. Settle the execution row so the probe
        // stops firing on every future open, and write no bubbles — the turn's own output was
        // never persisted, and inventing an error row for it would put a failure in the
        // transcript that never happened.
        if (IsAbandonedResume(snapshot))
        {
            _pendingInteractive.TryRemove(snapshot.ConversationId, out _);
            var recovered = await TryRecoverFinishedTurnAsync(snapshot).ConfigureAwait(false);
            await _conversations.FinalizeExecutionAsync(
                snapshot.RunId, snapshot.ConversationId, "",
                recovered ? "done" : "abandoned", 0).ConfigureAwait(false);
            await _connections.TrimIdleAsync(_runs.IsRunActive).ConfigureAwait(false);
            return;
        }

        var agentName = _agentsById.TryGetValue(snapshot.AgentId, out var a) ? a.Name : null;
        _pendingInteractive.TryRemove(snapshot.ConversationId, out _);
        _interruptRequestedRuns.TryRemove(snapshot.ConversationId, out _);

        var (window, nextId) = await LoadTurnWindowAsync(snapshot.ConversationId).ConfigureAwait(false);
        var target = new ListProjectionTarget(window, agentName, _http, nextId);
        var projection = new ChatTurnProjection(target);
        foreach (var e in snapshot.Events) projection.Apply(e);
        projection.CompleteToolActivity(
            snapshot.Status == ConversationRunStatus.Cancelled ? ToolActivityStatus.Cancelled : ToolActivityStatus.Failed,
            snapshot.ErrorMessage);
        if (target.PendingImages.Count > 0)
        {
            try { await Task.WhenAll(target.PendingImages).ConfigureAwait(false); } catch { /* best-effort */ }
        }

        // Keep whatever was generated, and surface the error inline (mirrors the old
        // "[connection error] …" bubble) so the failed turn is visible in history.
        if (snapshot.Status == ConversationRunStatus.Failed)
        {
            target.Add(new ChatMessage
            {
                Id = target.NextId(),
                Role = ChatRole.Agent,
                AgentName = agentName,
                Content = $"[connection error] {snapshot.ErrorMessage}",
            });
        }

        await ResolveInteractiveCardsAsync(snapshot.ConversationId, window).ConfigureAwait(false);

        await _conversations.UpsertMessagesAsync(
            snapshot.ConversationId, window.Where(m => !m.IsOnboarding).ToList()).ConfigureAwait(false);
        MarkHistoryPersisted(snapshot.RunId);

        // A failed or cancelled turn still burned tokens on every LLM call it managed to make, so
        // it is recorded exactly like a successful one. A ledger that only counts successes does
        // not add up — and the turns that fail late are often the expensive ones.
        await PersistUsageAsync(snapshot, agentName).ConfigureAwait(false);

        await _conversations.FinalizeExecutionAsync(
            snapshot.RunId, snapshot.ConversationId, snapshot.ErrorMessage ?? "", "error", 0).ConfigureAwait(false);

        await _connections.TrimIdleAsync(_runs.IsRunActive).ConfigureAwait(false);
    }

    /// <summary>
    /// The slice of the conversation a finished turn's projection actually needs, plus the id its
    /// first new bubble takes. The projection only ever appends bubbles or edits ones it created
    /// itself, with one exception: buffered <c>agent_image</c> events are flushed onto the last
    /// agent bubble at turn's end (see <c>ChatTurnProjection.FlushPendingImages</c>), which — for a
    /// turn that produced no output of its own — is this seed row. Seeding the list with that one
    /// row mirrors what the live list's tail holds, so the headless persist and the live view
    /// project identically, while the persist that follows writes only the rows in this window — a
    /// turn no longer costs a full read of the conversation and a full rewrite of every row in it.
    /// </summary>
    private async Task<(List<ChatMessage> Window, long NextId)> LoadTurnWindowAsync(string conversationId)
    {
        var nextId = await _conversations.GetNextMessageIdAsync(conversationId).ConfigureAwait(false);
        var lastAgent = await _conversations.LoadLastAgentMessageAsync(conversationId).ConfigureAwait(false);

        var window = new List<ChatMessage>();
        if (lastAgent is not null) window.Add(lastAgent);
        return (window, nextId);
    }

    /// <summary>
    /// Appends this run's token spend to the usage ledger. Runs on the app-level persist path, so a
    /// turn that completed with no page open (or with the window in the tray) is still accounted
    /// for — same reason the bubbles are written here rather than by a page.
    /// </summary>
    private Task PersistUsageAsync(ConversationRunSnapshot snapshot, string? agentName)
    {
        var rows = UsageProjector.Extract(snapshot, agentName);
        return rows.Count == 0 ? Task.CompletedTask : _usage.InsertAsync(rows);
    }

    private Task PersistTraceAsync(ConversationRunSnapshot snapshot)
    {
        var rows = new List<ConversationRepository.TraceEventRow>(snapshot.Events.Count);
        foreach (var e in snapshot.Events)
        {
            rows.Add(new ConversationRepository.TraceEventRow(
                EventId: e.EventId ?? Guid.NewGuid().ToString(),
                ExecutionId: snapshot.RunId,
                SessionId: snapshot.ConversationId,
                Type: e.Type,
                PayloadJson: TracePayload(e),
                Ts: e.Timestamp ?? TryParseTs(e.RawJson)));
        }
        return _conversations.InsertTraceEventsAsync(snapshot.ConversationId, rows);
    }

    private static string TracePayload(AgentStreamEvent e)
    {
        if (!string.Equals(e.Type, "agent_image", StringComparison.Ordinal)) return e.RawJson;

        // The decoded image is already persisted through message_attachments. Keeping the same
        // multi-megabyte data URL in trace_events duplicates it in SQLite and in the WAL without
        // adding diagnostic value; retain only the event identity and timestamp.
        var compact = new Dictionary<string, object?>
        {
            ["type"] = e.Type,
            ["id"] = e.EventId,
            ["ts"] = e.Timestamp,
            ["image_payload_omitted"] = true,
        };
        return WireJson.Serialize(compact);
    }

    private static double? TryParseTs(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("ts", out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Stamps the user's recorded interactive answers onto the cards this run just re-projected
    /// from the (reply-less) event stream, in creation order, then seals any that were never
    /// answered as <see cref="NoSelectionMeta"/>. Only freshly projected cards are touched —
    /// a card is "unresolved" iff it is still <see cref="EventStatus.Running"/> with no
    /// <c>EventMeta</c>, which cleanly skips interactive turns resolved in earlier turns of the
    /// same conversation (they already carry their own meta/status). This is what makes the
    /// chosen result — or "no selection" — survive into reloaded history.
    /// </summary>
    private async Task ResolveInteractiveCardsAsync(string conversationId, List<ChatMessage> history)
    {
        var answers = new Queue<RecordedInteractiveAnswer>(
            await _interactiveAnswers.DrainAsync(conversationId).ConfigureAwait(false));

        foreach (var m in history)
        {
            if (m.Role != ChatRole.Event
                || m.Status != EventStatus.Running
                || m.EventMeta is not null
                || m.EventKind is not ("ask_user" or "approval" or "plan_review"))
            {
                continue;
            }

            if (answers.TryDequeue(out var answer))
            {
                m.EventMeta = answer.Meta;
                m.Status = answer.Status;
            }
            else if (m.EventKind == "approval")
            {
                m.EventMeta = UnansweredApprovalMeta;
                m.Status = EventStatus.Error;
            }
            else
            {
                m.EventMeta = SkippedMeta;
                m.Status = EventStatus.Done;
            }

        }

        // A settled approval leaves the transcript entirely. The decision it was asking for has
        // been made, and what came of it is already on screen twice over — the tool-activity card
        // shows the step as Skipped/failed/done, and the agent's own reply speaks to it. Keeping a
        // third row that only restates the outcome (and, before this, dragged the whole request
        // payload into SQLite with it) was noise in every reloaded conversation.
        //
        // The stamping above still runs first: RecordInteractiveAnswer's queue is consumed in
        // creation order, so dropping the cards without dequeuing their answers would mis-align
        // every later ask_user/plan_review in the same turn.
        history.RemoveAll(m => m.Role == ChatRole.Event && m.EventKind == "approval");
    }

    /// <summary>Projection target backed by a plain list, used by the headless persistence
    /// path. Agent-image resolution is collected as awaitable tasks so the caller can await
    /// them before saving (the cache path must be known when the row is written).</summary>
    private sealed class ListProjectionTarget : IChatProjectionTarget
    {
        private readonly List<ChatMessage> _messages;
        private readonly HttpClient _http;
        private long _nextId;
        public readonly List<Task> PendingImages = new();

        /// <param name="nextId">The id for the first bubble this turn adds. Passed in (rather than
        /// derived from <paramref name="messages"/>) because the list is a window onto the tail of
        /// the conversation, not the whole of it — the max id lives in the database.</param>
        public ListProjectionTarget(List<ChatMessage> messages, string? agentName, HttpClient http, long nextId)
        {
            _messages = messages;
            AgentName = agentName;
            _http = http;
            _nextId = nextId;
        }

        public IList<ChatMessage> Messages => _messages;
        public string? AgentName { get; }
        // Nobody is watching this pass; persisted cards are written collapsed.
        public bool IsLiveView => false;
        public long NextId() => _nextId++;
        public void Add(ChatMessage message) => _messages.Add(message);

        public void ResolveAgentImage(string dataUrl, ChatAttachment attachment)
            => PendingImages.Add(ResolveAsync(dataUrl, attachment));

        private async Task ResolveAsync(string dataUrl, ChatAttachment attachment)
        {
            var resolved = await AttachmentImageCacheService.ResolveAndCacheAsync(dataUrl, _http).ConfigureAwait(false);
            if (resolved is { } value)
            {
                attachment.LocalCachePath = value.LocalPath;
                attachment.MimeType = value.MimeType;
                attachment.Status = AttachmentStatus.Sent;
            }
            else
            {
                attachment.Status = AttachmentStatus.Failed;
                attachment.Error = CoreStrings.Get(
                    "ImageLoadFailed",
                    "Image could not be loaded.");
            }
        }

        /// <summary>Ignored: this is the headless persistence pass, with nobody watching a live
        /// readout. The same figures still reach storage through the turn's summary row.</summary>
        public void ReportUsage(TurnUsage usage) { }
    }

}
