using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectOnion.Protocol;

/// <summary>
/// Native C# port of <c>remote-agent.ts</c>: a single persistent WebSocket
/// carrying the CONNECT/INPUT/OUTPUT protocol, PING/PONG keepalive, streamed
/// intermediate events, onboarding, and session reconnect.
///
/// Supports both connection modes the SDK does:
/// - Direct URL: connect straight to <c>ws(s)://host/ws</c>.
/// - Relay address: resolve a direct endpoint via the relay; if none is
///   reachable, fall back to the relay's <c>/ws/input</c> route and stamp every
///   message with <c>to: &lt;address&gt;</c> so the relay routes it.
///
/// Threading model mirrors the SDK's promise machine with
/// <see cref="TaskCompletionSource{TResult}"/>: <see cref="EnsureConnectedAsync"/>
/// completes on CONNECTED, <see cref="SendInputAsync"/> completes on OUTPUT. A
/// background receive loop dispatches frames; a watchdog closes the socket after
/// 60s of silence (the only bounded wait, matching the SDK — an input turn has
/// no wall-clock deadline because ask_user legitimately waits on a human).
/// </summary>
public sealed class AgentConnectionService : IAsyncDisposable
{
    private static readonly TimeSpan SocketCloseTimeout = TimeSpan.FromSeconds(1.5);
    private const int ConnectTimeoutMs = 30_000;
    private const int SilenceTimeoutMs = 60_000;
    private const int WatchdogIntervalMs = 10_000;

    private readonly string _address;
    private readonly string? _directUrl;
    private readonly string _relayUrl;
    private readonly AgentIdentity _identity;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _silenceTimeout;
    private readonly TimeSpan _watchdogInterval;

    // Resolved lazily on first connect. isDirect=false means the relay /ws/input
    // path, where every message must carry `to`.
    private string? _wsUrl;
    private bool _isDirect;
    private bool _resolutionAttempted;

    private ClientWebSocket? _ws;
    private Task? _receiveLoop;
    private CancellationTokenSource? _loopCts;
    private bool _authenticated;
    private DateTime _lastActivityUtc;
    private volatile bool _awaitingHumanResponse;

    private TaskCompletionSource<WireMessage>? _connectTcs;
    private TaskCompletionSource<string>? _inputTcs;
    private TaskCompletionSource<string>? _sessionStatusTcs;
    private CancellationTokenSource? _connectTimeoutCts;

    // ---- Automatic reconnect ----
    private readonly ReconnectPolicy _reconnectPolicy;
    // Guards against a second reconnect loop: the attempts themselves fail sockets, and each
    // failure re-enters HandleConnectionLoss. 0 = idle, 1 = a loop owns the reconnect.
    private int _reconnectLoopActive;
    private volatile bool _disposed;
    /// <summary>Frames written while the socket was down, replayed in order once it is back.
    /// This is what lets a user answer an ask_user during an outage — the answer waits for the
    /// socket rather than vanishing into a failed send.</summary>
    private readonly ConcurrentQueue<Dictionary<string, object?>> _outbound = new();

    public string? SessionId { get; private set; }

    /// <summary>
    /// The approval mode to put in the CONNECT frame's <c>session</c> object. This only
    /// lands for a session the host has never run before: on any later CONNECT the host's
    /// <c>merge_sessions</c> compares iteration counts and replaces the client's session
    /// wholesale with its own (we report no iteration, so it always wins). For an existing
    /// session the mode must therefore ride along with an INPUT — see
    /// <see cref="SendInputAsync"/> — or be pushed mid-turn with <see cref="SetModeAsync"/>.
    /// </summary>
    public string? InitialMode { get; set; }

    /// <summary>
    /// Last fully-processed stream event ID. Sent as <c>last_msg_id</c> on
    /// reconnect so the server can replay only events the client hasn't seen.
    /// </summary>
    public string? LastProcessedEventId { get; private set; }

    /// <summary>The duration (ms) of the most recent execution, from the OUTPUT frame.</summary>
    public double LastExecutionDurationMs { get; private set; }

    /// <summary>The host's most recent CONNECTED verdict, or null before the first handshake.</summary>
    public ConnectedState? LastConnectedState { get; private set; }

    public bool IsConnected => _ws is { State: WebSocketState.Open } && _authenticated;

    /// <summary>
    /// Invite code submitted automatically if the agent's trust gate responds
    /// with ONBOARD_REQUIRED. Null means no onboarding is attempted.
    /// </summary>
    public string? InviteCode { get; set; }

    /// <summary>Streamed intermediate events (thinking, tool_call, …) for status UI.</summary>
    public event Action<AgentStreamEvent>? StreamEvent;

    /// <summary>Raised immediately after an INPUT frame is written to the live socket.</summary>
    public event Action? InputSent;

    /// <summary>Raised when the connection drops unexpectedly and is not coming back — either
    /// reconnect is not applicable, or all <see cref="ReconnectPolicy.MaxAttempts"/> attempts
    /// failed. A drop that the reconnect loop is still working on raises
    /// <see cref="Reconnecting"/> instead, so a subscriber can trust this to mean "gone".</summary>
    public event Action<Exception>? ConnectionLost;

    /// <summary>Raised before each reconnect attempt, with the attempt number and the wait
    /// ahead of it. Purely informational — the socket is down for the duration.</summary>
    public event Action<ReconnectingEvent>? Reconnecting;

    /// <summary>Raised once the socket is authenticated again, carrying the host's verdict on
    /// the session. <see cref="ConnectedState.IsRunning"/> means the host has already rewound
    /// to our <c>last_msg_id</c> and is replaying the events we missed into the turn that is
    /// still open — the caller must not re-send the prompt.</summary>
    public event Action<ConnectedState>? Reconnected;

    /// <summary>Raised when the host's session merge came back <c>server_newer</c>: its copy of
    /// the conversation was ahead of ours and it has handed back the authoritative one.</summary>
    public event Action<ConnectedState>? SessionDiverged;

    /// <summary>The agent is waiting for a human answer (ask_user).</summary>
    public event Action<AskUserRequest>? AskUserRequested;

    /// <summary>The agent requires onboarding before CONNECT can complete.</summary>
    /// <summary>Raised when the agent's trust gate hands the connection to a human. The payload
    /// says which methods it will accept, so the card can offer them rather than assuming an
    /// invite code.</summary>
    public event Action<OnboardRequest>? OnboardRequired;

    /// <summary>The agent wants approval to run a tool (approval_needed).</summary>
    public event Action<ApprovalRequest>? ApprovalRequested;

    /// <summary>The agent wants its plan reviewed (plan_review).</summary>
    public event Action<PlanReviewRequest>? PlanReviewRequested;

    /// <summary>The agent's approval mode changed — either because we asked
    /// (<c>triggered_by: "user"</c>) or because the agent switched itself into plan mode.</summary>
    public event Action<ModeChangedEvent>? ModeChanged;

    /// <summary>The host queued a mid-execution prompt (our INPUT reached a session that already
    /// had an agent running, so it became a RUNTIME_INPUT rather than a new turn).</summary>
    public event Action? RuntimeInputAcknowledged;

    /// <summary>
    /// Creates a connection. Provide <paramref name="directUrl"/> for the direct
    /// path, or leave it null to connect by relay address. <paramref name="http"/>
    /// is used for relay endpoint resolution; a shared instance is recommended.
    /// </summary>
    public AgentConnectionService(
        string address,
        string? directUrl,
        AgentIdentity identity,
        HttpClient? http = null,
        string relayUrl = EndpointResolver.DefaultRelay,
        TimeSpan? connectTimeout = null,
        TimeSpan? silenceTimeout = null,
        TimeSpan? watchdogInterval = null,
        ReconnectPolicy? reconnectPolicy = null)
    {
        _reconnectPolicy = reconnectPolicy ?? new ReconnectPolicy();
        _address = address;
        _directUrl = string.IsNullOrWhiteSpace(directUrl) ? null : directUrl.TrimEnd('/');
        _identity = identity;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _relayUrl = EndpointResolver.NormalizeRelayUrl(relayUrl);
        _connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(ConnectTimeoutMs);
        _silenceTimeout = silenceTimeout ?? TimeSpan.FromMilliseconds(SilenceTimeoutMs);
        _watchdogInterval = watchdogInterval ?? TimeSpan.FromMilliseconds(WatchdogIntervalMs);
    }

    // Port of _resolveWsUrl + _resolveEndpointOnce. Direct URL wins; otherwise
    // try to resolve a direct endpoint through the relay, else use the relay's
    // /ws/input route (isDirect=false).
    private async Task ResolveConnectionAsync(CancellationToken ct)
    {
        if (_wsUrl is not null) return;

        if (_directUrl is not null)
        {
            _wsUrl = DirectWsUrl(_directUrl);
            _isDirect = true;
            return;
        }

        if (!_resolutionAttempted && _address.StartsWith("0x", StringComparison.Ordinal) && _address.Length == 66)
        {
            _resolutionAttempted = true;
            var resolved = await EndpointResolver
                .ResolveEndpointAsync(_http, _address, _relayUrl)
                .ConfigureAwait(false);
            if (resolved is not null)
            {
                _wsUrl = resolved.WsUrl;
                _isDirect = true;
                return;
            }
        }

        // Fall back to the relay input socket; messages carry `to`.
        _wsUrl = $"{_relayUrl}/ws/input";
        _isDirect = false;
    }

    private static string DirectWsUrl(string directUrl)
    {
        if (!Uri.TryCreate(directUrl, UriKind.Absolute, out var uri))
        {
            throw new UriFormatException("Invalid Direct URL.");
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = uri.AbsolutePath.TrimEnd('/') + "/ws",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri.ToString();
    }

    /// <summary>
    /// Sends a prompt and resolves with the final OUTPUT text. Opens and
    /// authenticates the socket first if needed. No wall-clock timeout by design.
    /// <paramref name="images"/> and <paramref name="files"/> follow the exact
    /// wire contract in <see cref="InputMessageBuilder"/> — omit both for a
    /// text-only send identical to the original signature.
    /// </summary>
    public async Task<string> SendInputAsync(
        string prompt,
        string? sessionId,
        IReadOnlyList<string>? images = null,
        IReadOnlyList<OutgoingFileAttachment>? files = null,
        string? mode = null,
        CancellationToken ct = default)
    {
        var completion = await BeginInputAsync(prompt, sessionId, images, files, mode, ct)
            .ConfigureAwait(false);
        return await completion.ConfigureAwait(false);
    }

    /// <summary>
    /// Sends INPUT and returns the separate task that completes on OUTPUT. Callers handling large
    /// attachments can release their encoded lists as soon as this method returns instead of
    /// retaining them for the entire agent turn.
    /// </summary>
    public async Task<Task<string>> BeginInputAsync(
        string prompt,
        string? sessionId,
        IReadOnlyList<string>? images = null,
        IReadOnlyList<OutgoingFileAttachment>? files = null,
        string? mode = null,
        CancellationToken ct = default)
    {
        // Carried into the CONNECT below when this is the socket's first turn.
        if (mode is not null) InitialMode = mode;

        await EnsureConnectedAsync(sessionId, ct).ConfigureAwait(false);

        var inputTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inputTcs = inputTcs;

        var inputMsg = InputMessageBuilder.BuildInput(
            prompt,
            Guid.NewGuid().ToString(),
            toAddress: _isDirect ? null : _address,
            images: images,
            files: files);
        await SendJsonAsync(inputMsg, ct).ConfigureAwait(false);
        inputMsg.Clear();
        InputSent?.Invoke();

        // Mode has to be (re)asserted *after* the INPUT, never before. The host only forwards
        // a mode_change to the agent while a turn owns the connection (`active_io` in its
        // ws_router); sent on an idle socket it is dropped on the floor. Sending it here — on
        // the same socket, immediately behind the INPUT that spawns the agent — means the
        // host's sequential read loop has an agent to hand it to, and the agent's
        // `poll_mode_changes` (a before_iteration hook) picks it up at its first iteration
        // boundary. This is also why a CONNECT-only mode is not enough for a session the host
        // already knows: its session merge would have thrown ours away.
        if (mode is not null) await SendModeChangeAsync(mode, null, ct).ConfigureAwait(false);

        return AwaitInputAsync(inputTcs, ct);
    }

    private static async Task<string> AwaitInputAsync(
        TaskCompletionSource<string> inputTcs,
        CancellationToken ct)
    {
        await using (ct.Register(() => inputTcs.TrySetCanceled(ct)))
#pragma warning disable VSTHRD003 // This TCS is the protocol operation; cancellation is wired above.
            return await inputTcs.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
    }

    /// <summary>
    /// Attaches to a turn the host is <i>already running</i> and resolves with its OUTPUT,
    /// without sending an INPUT. This is how a restarted client rejoins a turn it started
    /// before it was closed.
    ///
    /// <para>The host does the replaying: CONNECT carries our session id, and because a
    /// restarted client has no <see cref="LastProcessedEventId"/> (the id lives in memory, and
    /// the turn's trace was never persisted — it is written in one batch at the end),
    /// <c>rewind_to(null)</c> rewinds to the very start of its buffer and forwards the whole
    /// turn again. Everything the agent has emitted, including a still-pending <c>ask_user</c>
    /// or <c>approval_needed</c>, therefore arrives as ordinary stream events and projects into
    /// the usual cards.</para>
    ///
    /// <para>Throws <see cref="SessionNotRunningException"/> if the host says the session is
    /// anything other than <see cref="ConnectedStatuses.Running"/> — the agent finished or died
    /// while we were away, and there is nothing to attach to.</para>
    /// </summary>
    public async Task<string> ResumeRunningSessionAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        // Armed *before* the handshake, not after: the host calls resume_forwarding immediately
        // behind CONNECTED, so a short turn can emit its OUTPUT before EnsureConnectedAsync has
        // even returned. With the slot still null that frame would be dropped on the floor in
        // HandleMessageAsync and this method would wait forever.
        var resumeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inputTcs = resumeTcs;

        try
        {
            await EnsureConnectedAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch
        {
            _inputTcs = null;
            throw;
        }

        if (LastConnectedState?.IsRunning != true)
        {
            _inputTcs = null;
            throw new SessionNotRunningException(LastConnectedState?.Status ?? ConnectedStatuses.New);
        }

        await using (ct.Register(() => resumeTcs.TrySetCanceled()))
        {
            return await resumeTcs.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Switches the agent's approval mode on a live turn. Takes effect at the agent's next
    /// iteration boundary, not instantly (the host polls for these between LLM calls), and is
    /// silently dropped by the host if no turn is currently running — so callers must also pass
    /// the mode to the next <see cref="SendInputAsync"/>, which is what makes it stick.
    /// </summary>
    public Task SetModeAsync(string mode, int? turns = null, CancellationToken ct = default)
        => IsConnected ? SendModeChangeAsync(mode, turns, ct) : Task.CompletedTask;

    private Task SendModeChangeAsync(string mode, int? turns, CancellationToken ct)
    {
        var msg = new Dictionary<string, object?>
        {
            ["type"] = "mode_change",
            ["mode"] = mode,
        };
        if (turns is { } t) msg["turns"] = t;
        if (!_isDirect) msg["to"] = _address;
        return SendJsonAsync(msg, ct);
    }

    /// <summary>Sends the exact graceful-stop frame on the current established socket. The
    /// pending <see cref="SendInputAsync"/> remains open until the host returns OUTPUT/ERROR.</summary>
    public Task SendInterruptAsync(CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Cannot stop: the agent connection is not open.");
        return SendJsonAsync(new Dictionary<string, object?> { ["type"] = "INTERRUPT" }, ct);
    }

    /// <summary>
    /// Sends an additional INPUT while the current turn is still awaiting OUTPUT. The host routes
    /// this frame to the running agent as RUNTIME_INPUT; unlike <see cref="SendInputAsync"/>, this
    /// method deliberately does not replace the pending OUTPUT completion source or start another
    /// client-side turn.
    /// </summary>
    public Task SendRuntimeInputAsync(
        string prompt,
        IReadOnlyList<string>? images = null,
        IReadOnlyList<OutgoingFileAttachment>? files = null,
        CancellationToken ct = default)
    {
        if (!IsConnected || _inputTcs is null)
            throw new InvalidOperationException("Cannot send runtime input: no agent turn is active.");

        var inputMsg = InputMessageBuilder.BuildInput(
            prompt,
            Guid.NewGuid().ToString(),
            toAddress: _isDirect ? null : _address,
            images: images,
            files: files);
        return SendJsonAsync(inputMsg, ct);
    }

    /// <summary>
    /// Asks the host what it thinks of a session: <see cref="SessionStatuses.Running"/>,
    /// <see cref="SessionStatuses.Connected"/>, or <see cref="SessionStatuses.NotFound"/>.
    /// Answers <see cref="SessionStatuses.NotFound"/> if there is no socket or no reply in time.
    /// </summary>
    public async Task<string> QuerySessionStatusAsync(string sessionId, CancellationToken ct = default)
    {
        if (!IsConnected) return SessionStatuses.NotFound;

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionStatusTcs = tcs;

        var msg = new Dictionary<string, object?>
        {
            ["type"] = "SESSION_STATUS",
            ["session"] = new Dictionary<string, object?> { ["session_id"] = sessionId },
        };
        if (!_isDirect) msg["to"] = _address;

        try
        {
            await SendJsonAsync(msg, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using (timeout.Token.Register(() => tcs.TrySetResult(SessionStatuses.NotFound)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        catch
        {
            return SessionStatuses.NotFound;
        }
        finally
        {
            _sessionStatusTcs = null;
        }
    }

    /// <summary>Force-reconnects to an existing session. Port of <c>reconnect()</c>.</summary>
    public async Task ReconnectAsync(string sessionId, CancellationToken ct = default)
    {
        await CloseSocketAsync().ConfigureAwait(false);
        SessionId = sessionId;
        await EnsureConnectedAsync(sessionId, ct).ConfigureAwait(false);
    }

    private async Task EnsureConnectedAsync(string? sessionId, CancellationToken ct)
    {
        if (_ws is { State: WebSocketState.Open } && _authenticated) return;

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(_connectTimeout);
        var connectToken = connectCts.Token;

        await ResolveConnectionAsync(connectToken).ConfigureAwait(false);

        _loopCts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(_wsUrl!), connectToken).ConfigureAwait(false);

        _lastActivityUtc = DateTime.UtcNow;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_loopCts.Token), CancellationToken.None);
        _ = Task.Run(() => WatchdogAsync(_loopCts.Token), CancellationToken.None);

        var connectTcs = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectTcs = connectTcs;

        // CONNECT payload is signed: { timestamp, to }.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var envelope = _identity.SignPayload(new List<KeyValuePair<string, object?>>
        {
            new("timestamp", timestamp),
            new("to", _address),
        });

        var connectMsg = new Dictionary<string, object?>
        {
            ["type"] = "CONNECT",
            ["payload"] = envelope.Payload,
            ["from"] = envelope.From,
            ["signature"] = envelope.Signature,
            ["timestamp"] = envelope.Timestamp,
        };
        // Relay path: the relay needs `to` to route the frame to the agent.
        if (!_isDirect) connectMsg["to"] = _address;
        if (!string.IsNullOrEmpty(sessionId))
        {
            connectMsg["session_id"] = sessionId;
            var session = new Dictionary<string, object?> { ["session_id"] = sessionId };
            // Only decides the mode for a session the host has never run (see InitialMode):
            // otherwise its merge keeps the server's copy. Harmless either way, and it is the
            // only way a brand-new conversation starts its very first turn in the right mode.
            if (InitialMode is not null) session["mode"] = InitialMode;
            connectMsg["session"] = session;
            if (!string.IsNullOrEmpty(LastProcessedEventId))
                connectMsg["last_msg_id"] = LastProcessedEventId;
        }

        await SendJsonAsync(connectMsg, connectToken).ConfigureAwait(false);

        // The connect deadline is the only bounded wait; ONBOARD_REQUIRED
        // cancels it (a human/onboarding step now owns the connection).
        _connectTimeoutCts = new CancellationTokenSource(_connectTimeout);
        try
        {
            await using (_connectTimeoutCts.Token.Register(() =>
                             connectTcs.TrySetException(new TimeoutException("Authentication timed out"))))
            await using (connectToken.Register(() => connectTcs.TrySetCanceled()))
            {
                var connected = await connectTcs.Task.ConfigureAwait(false);
                _authenticated = true;
                SessionId = connected.GetString("session_id") ?? sessionId;
            }
        }
        finally
        {
            // This field means "a handshake is in flight" to TryStartReconnect. Leaving the
            // completed TCS here permanently made every later transport loss look like a failed
            // initial handshake and silently disabled automatic reconnect for established turns.
            _connectTcs = null;
            _connectTimeoutCts?.Dispose();
            _connectTimeoutCts = null;
        }
    }

    public Task SubmitOnboardInviteCodeAsync(string inviteCode, CancellationToken ct = default)
    {
        InviteCode = inviteCode;
        return SubmitOnboardAsync(inviteCode, null, ct);
    }

    /// <summary>
    /// Answers the payment branch of an onboarding gate: the user says they have transferred
    /// <paramref name="amount"/>, and the agent verifies it independently.
    ///
    /// <para>Not stored the way <see cref="InviteCode"/> is, and deliberately so: an invite code is
    /// a reusable credential worth re-submitting automatically on the next reconnect, whereas a
    /// payment claim is a one-off assertion about a transfer that has already happened. Re-asserting
    /// it on every reconnect would tell the agent about a second payment that was never made.</para>
    /// </summary>
    public Task SubmitOnboardPaymentAsync(double amount, CancellationToken ct = default)
        => SubmitOnboardAsync(null, amount, ct);

    // Port of signOnboard(): a signed { timestamp, invite_code?, payment? } envelope. The two
    // optional fields are omitted rather than sent null — the host reads presence, and a null
    // invite_code beside a payment would look like an empty code being offered.
    private async Task SubmitOnboardAsync(string? inviteCode, double? payment, CancellationToken ct)
    {
        // The human answered; hand the silence budget back to the agent.
        ResumeWatchdogAfterHumanResponse();
        var payload = new List<KeyValuePair<string, object?>>
        {
            new("timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        };
        if (!string.IsNullOrEmpty(inviteCode)) payload.Add(new("invite_code", inviteCode));
        if (payment is > 0) payload.Add(new("payment", payment.Value));
        var envelope = _identity.SignPayload(payload);
        await SendJsonAsync(new Dictionary<string, object?>
        {
            ["type"] = "ONBOARD_SUBMIT",
            ["payload"] = envelope.Payload,
            ["from"] = envelope.From,
            ["signature"] = envelope.Signature,
            ["timestamp"] = envelope.Timestamp,
        }, ct).ConfigureAwait(false);
    }

    // --- Interactive-turn responses (sent on the live socket; the pending
    // SendInputAsync stays open until the agent finally emits OUTPUT). ---

    /// <summary>Answers an ask_user turn. Port of the ASK_USER_RESPONSE message.</summary>
    public Task RespondAskUserAsync(object answer)
    {
        ResumeWatchdogAfterHumanResponse();
        return SendOrQueueAsync(new Dictionary<string, object?>
        {
            ["answer"] = answer,
        }, CancellationToken.None);
    }

    /// <summary>
    /// Responds to an approval_needed turn. <paramref name="scope"/> is "once" or "session".
    ///
    /// <paramref name="rejectMode"/> decides what a rejection <i>does</i>, and the default is
    /// deliberately <see cref="ApprovalRejectModes.Hard"/> — matching the host's own default.
    /// Hard is the only frame in the entire protocol that halts a running agent: it sets
    /// <c>stop_signal</c>, which rejects the rest of the tool batch and breaks the iteration
    /// loop. Soft/Explain merely skip the one tool and let the agent carry on, which is not
    /// what a user who clicked "Reject" means.
    /// </summary>
    public Task RespondApprovalAsync(
        bool approved,
        string scope = "once",
        string rejectMode = ApprovalRejectModes.Hard,
        string? feedback = null)
    {
        ResumeWatchdogAfterHumanResponse();
        var msg = new Dictionary<string, object?>
        {
            ["approved"] = approved,
        };
        if (approved) msg["scope"] = scope;
        if (!approved)
        {
            msg["mode"] = rejectMode;
            if (!string.IsNullOrEmpty(feedback)) msg["feedback"] = feedback;
        }
        return SendOrQueueAsync(msg, CancellationToken.None);
    }

    /// <summary>Responds to a plan_review turn. Port of PLAN_REVIEW_RESPONSE.</summary>
    public Task RespondPlanReviewAsync(string message)
    {
        ResumeWatchdogAfterHumanResponse();
        return SendOrQueueAsync(new Dictionary<string, object?>
        {
            ["message"] = message,
        }, CancellationToken.None);
    }

    private void ResumeWatchdogAfterHumanResponse()
    {
        // A human can legitimately leave the turn idle past the silence timeout.
        // Once they respond, give the agent a fresh timeout window instead of
        // counting the time spent waiting for the human as server silence.
        _lastActivityUtc = DateTime.UtcNow;
        _awaitingHumanResponse = false;
    }

    private static AskUserRequest ParseAskUser(WireMessage msg) => AgentInteractiveParsers.ParseAskUser(msg);

    /// <summary>
    /// A 50 MiB agent image expands to about 66.7 MiB as base64. Leave room for its data-URL
    /// prefix and JSON envelope, but reject an unbounded or never-ending frame before it can
    /// exhaust the desktop process.
    /// </summary>
    public const int MaxIncomingFrameBytes = 80 * 1024 * 1024;
    private const int RetainedIncomingFrameCapacityBytes = 1024 * 1024;

    internal int IncomingFrameSizeLimitBytes { get; set; } = MaxIncomingFrameBytes;

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var ws = _ws!;
        var buffer = new byte[16 * 1024];
        var frame = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    HandleConnectionLoss(new WebSocketException("Server closed the connection"));
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await ws.CloseOutputAsync(
                        WebSocketCloseStatus.InvalidMessageType,
                        "Only text protocol frames are supported.",
                        CancellationToken.None).ConfigureAwait(false);
                    HandleConnectionLoss(new WebSocketException("Server sent a non-text protocol frame"));
                    return;
                }

                if (frame.Length + result.Count > IncomingFrameSizeLimitBytes)
                {
                    await ws.CloseOutputAsync(
                        WebSocketCloseStatus.MessageTooBig,
                        "Incoming protocol frame exceeded the client limit.",
                        CancellationToken.None).ConfigureAwait(false);
                    HandleConnectionLoss(new WebSocketException(
                        $"Incoming WebSocket frame exceeded {IncomingFrameSizeLimitBytes} bytes"));
                    return;
                }

                frame.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                // JSON is already UTF-8 on the wire. Parse this buffer in place while it remains
                // owned by the loop; turning an 80 MiB frame into a UTF-16 string allocated up to
                // another 160 MiB before JsonDocument copied it back into UTF-8 storage.
                var json = new ReadOnlyMemory<byte>(
                    frame.GetBuffer(),
                    0,
                    checked((int)frame.Length));
                _lastActivityUtc = DateTime.UtcNow;
                await HandleMessageAsync(json, ct).ConfigureAwait(false);

                if (frame.Capacity > RetainedIncomingFrameCapacityBytes)
                {
                    frame.Dispose();
                    frame = new MemoryStream();
                }
                else
                {
                    frame.SetLength(0);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Intentional close via CloseSocketAsync/Dispose.
        }
        catch (Exception ex)
        {
            HandleConnectionLoss(ex);
        }
        finally
        {
            frame.Dispose();
        }
    }

    // Port of _handleMessage: dispatch on the frame's type discriminator.
    private async Task HandleMessageAsync(ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        JsonDocument document;
        try { document = WireMessage.ParseDocument(json); }
        catch { return; }
        using var documentOwner = document;
        var msg = WireMessage.Wrap(document.RootElement);

        switch (msg.Type)
        {
            case "PING":
                await SendJsonAsync(new Dictionary<string, object?> { ["type"] = "PONG" }, ct).ConfigureAwait(false);
                return;

            case "CONNECTED":
                {
                    var state = ConnectedState.Parse(msg);
                    LastConnectedState = state;
                    // Raised before the connect promise resolves so a subscriber that reconciles
                    // history has done so by the time the caller's SendInputAsync continues.
                    if (state.ServerNewer) SessionDiverged?.Invoke(state);
                    // The transient receive document is disposed when this handler returns, while
                    // RunContinuationsAsynchronously means EnsureConnectedAsync reads the message
                    // later. CONNECTED is tiny, so clone only this one cross-boundary frame.
                    _connectTcs?.TrySetResult(WireMessage.Parse(msg.Root.GetRawText()));
                    return;
                }

            case "OUTPUT":
                {
                    _awaitingHumanResponse = false;
                    var result = msg.GetString("result") ?? "";
                    if (msg.TryGet("duration_ms", out var d) && d.ValueKind == JsonValueKind.Number)
                        LastExecutionDurationMs = d.GetDouble();
                    _inputTcs?.TrySetResult(result);
                    return;
                }

            case "ERROR":
                // Whatever the agent was parked on is over — don't leave the watchdog suspended.
                _awaitingHumanResponse = false;
                var message = msg.GetString("message") ?? msg.GetString("error") ?? "Unknown error";
                var error = new InvalidOperationException($"Agent error: {message}");
                _connectTcs?.TrySetException(error);
                _inputTcs?.TrySetException(error);
                return;

            // Streamed intermediate events → surface for status UI.
            case "thinking":
            case "llm_call":
            case "llm_result":
            case "tool_call":
            case "tool_result":
            case "assistant":
            case "agent_image":
            case "intent":
            case "eval":
            case "compact":
            case "tool_blocked":
            case "files_received":
            // Informational only — DiffWriter sends it and moves straight on to a separate
            // ask_user carrying the actual approve/reject options. It renders as a read-only
            // diff card; answering *this* frame would send a response nothing is waiting on and
            // leave the real ask_user blocked forever. See diff_writer.py `_send_preview`.
            case "diff_preview":
            case "session_sync":
                {
                    var eventId = msg.GetString("id");
                    if (!string.IsNullOrEmpty(eventId))
                        LastProcessedEventId = eventId;
                    StreamEvent?.Invoke(new AgentStreamEvent(
                        msg.Type,
                        DescribeEvent(msg),
                        eventId,
                        BufferedEventJson(msg),
                        EventTimestamp(msg.Root)));
                    return;
                }

            case "ONBOARD_REQUIRED":
                // A human/onboarding step now owns the connection, so stop the
                // 30s auth deadline. If we hold an invite code, submit it; the
                // follow-up CONNECTED then resolves the pending connect.
                _connectTimeoutCts?.CancelAfter(Timeout.InfiniteTimeSpan);
                StreamEvent?.Invoke(new AgentStreamEvent(msg.Type, DescribeEvent(msg), null, msg.Root.GetRawText()));
                if (!string.IsNullOrEmpty(InviteCode))
                {
                    await SubmitOnboardAsync(InviteCode, null, ct).ConfigureAwait(false);
                }
                else
                {
                    // Same deal as ask_user: the socket is now parked on a human typing an
                    // invite code, which routinely takes longer than the 60s silence timeout.
                    // Without this the watchdog kills the connection mid-onboarding and the
                    // user sees "offline / trying to reconnect" instead of their own prompt.
                    _awaitingHumanResponse = true;
                    OnboardRequired?.Invoke(AgentInteractiveParsers.ParseOnboard(msg));
                }
                return;

            case "ONBOARD_SUCCESS":
                // No client retry: the host finishes the interrupted CONNECT and
                // sends CONNECTED, which resolves the connect promise.
                StreamEvent?.Invoke(new AgentStreamEvent(msg.Type, DescribeEvent(msg), null, msg.Root.GetRawText()));
                return;

            case "ask_user":
                _awaitingHumanResponse = true;
                AskUserRequested?.Invoke(ParseAskUser(msg));
                return;

            case "approval_needed":
                _awaitingHumanResponse = true;
                ApprovalRequested?.Invoke(new ApprovalRequest(
                    msg.GetString("tool") ?? "",
                    msg.GetString("description"),
                    msg.TryGet("arguments", out var argsEl) ? argsEl.GetRawText() : "{}",
                    msg.GetString("reason"),
                    msg.TryGet("batch_remaining", out var batchEl) ? batchEl.GetRawText() : null));
                return;

            case "plan_review":
                _awaitingHumanResponse = true;
                PlanReviewRequested?.Invoke(new PlanReviewRequest(msg.GetString("plan_content") ?? ""));
                return;

            case "mode_changed":
                {
                    // Forwarded as a stream event *as well as* a typed one: the stream copy is what
                    // the run buffers and the projection turns into a visible "Mode: plan" card, so a
                    // mode the agent chose for itself is never a silent change.
                    var mode = msg.GetString("mode");
                    if (string.IsNullOrEmpty(mode)) return;
                    var eventId = msg.GetString("id");
                    if (!string.IsNullOrEmpty(eventId)) LastProcessedEventId = eventId;
                    StreamEvent?.Invoke(new AgentStreamEvent(
                        msg.Type, DescribeEvent(msg), eventId, msg.Root.GetRawText(), EventTimestamp(msg.Root)));
                    ModeChanged?.Invoke(new ModeChangedEvent(mode, msg.GetString("triggered_by")));
                    return;
                }

            case "RUNTIME_INPUT_ACK":
                RuntimeInputAcknowledged?.Invoke();
                return;

            case "SESSION_STATUS":
                _sessionStatusTcs?.TrySetResult(msg.GetString("status") ?? SessionStatuses.NotFound);
                return;

            default:
                return;
        }
    }

    /// <summary>
    /// Returns the event payload retained by the run runtime. Most event kinds need their full
    /// frame for projection and trace persistence. <c>session_sync</c> is different: the host
    /// includes the entire session message history, trace, and permission table on every sync,
    /// while the client consumes only the current mode. Retaining every full sync makes one long
    /// turn hold a growing series of near-duplicate session snapshots.
    /// </summary>
    internal static string BufferedEventJson(WireMessage msg)
    {
        if (msg.Type is "llm_call" or "llm_result") return CompactLlmEvent(msg);
        if (msg.Type == "tool_result") return CompactToolResult(msg);
        if (msg.Type != "session_sync") return msg.Root.GetRawText();

        var compact = new Dictionary<string, object?>
        {
            ["type"] = "session_sync",
        };
        CopyScalar(msg.Root, compact, "id");
        CopyScalar(msg.Root, compact, "ts");
        CopyScalar(msg.Root, compact, "session_id");

        if (msg.TryGet("session", out var session) && session.ValueKind == JsonValueKind.Object)
        {
            var sessionState = new Dictionary<string, object?>();
            CopyScalar(session, sessionState, "session_id");
            CopyScalar(session, sessionState, "mode");
            CopyScalar(session, sessionState, "turn");
            CopyScalar(session, sessionState, "iteration");
            compact["session"] = sessionState;
        }

        return WireJson.Serialize(compact);
    }

    private static string CompactToolResult(WireMessage msg)
    {
        const int maxRetainedResultChars = 8 * 1024;
        var compact = new Dictionary<string, object?> { ["type"] = msg.Type };
        foreach (var name in new[]
                 {
                     "id", "ts", "tool_id", "call_id", "name", "tool", "status",
                     "timing_ms", "duration_ms",
                 })
        {
            CopyScalar(msg.Root, compact, name);
        }

        foreach (var name in new[] { "result", "message", "error" })
        {
            var value = msg.GetString(name);
            if (value is null) continue;
            compact[name] = value.Length <= maxRetainedResultChars
                ? value
                : value[..maxRetainedResultChars] + "\n… (truncated)";
        }
        return WireJson.Serialize(compact);
    }

    private static string CompactLlmEvent(WireMessage msg)
    {
        var compact = new Dictionary<string, object?> { ["type"] = msg.Type };
        foreach (var name in new[]
                 {
                     "id", "ts", "model", "duration_ms", "context_percent",
                     "tool_calls_count", "context_before", "context_after",
                 })
        {
            CopyScalar(msg.Root, compact, name);
        }

        if (msg.TryGet("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var compactUsage = new Dictionary<string, object?>();
            foreach (var name in new[]
                     {
                         "input_tokens", "output_tokens", "cached_tokens", "cache_write_tokens",
                     })
            {
                CopyScalar(usage, compactUsage, name);
            }
            compact["usage"] = compactUsage;
        }

        return WireJson.Serialize(compact);
    }

    private static void CopyScalar(
        JsonElement source,
        Dictionary<string, object?> destination,
        string name)
    {
        if (!source.TryGetProperty(name, out var value)) return;

        destination[name] = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null,
        };
    }

    private static double? EventTimestamp(JsonElement root)
        => root.TryGetProperty("ts", out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var timestamp)
            ? timestamp
            : null;

    private static string DescribeEvent(WireMessage msg) => msg.Type switch
    {
        "tool_call" => $"Running tool: {msg.GetString("name")}",
        "tool_result" => $"Tool finished: {msg.GetString("name")}",
        "thinking" => "Thinking…",
        "mode_changed" => $"Mode: {AgentModes.DisplayName(msg.GetString("mode"))}",
        "ask_user" => msg.GetString("text") ?? msg.GetString("question") ?? "Agent asked a question",
        "diff_preview" => $"Proposed change: {msg.GetString("path")}",
        "ONBOARD_REQUIRED" => "Agent requires onboarding",
        _ => msg.Type,
    };

    private async Task WatchdogAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_watchdogInterval, ct).ConfigureAwait(false);
                if (!_awaitingHumanResponse && DateTime.UtcNow - _lastActivityUtc > _silenceTimeout)
                {
                    HandleConnectionLoss(new TimeoutException("Connection went silent"));
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void HandleConnectionLoss(Exception ex)
    {
        _authenticated = false;
        _awaitingHumanResponse = false;
        // A status query is a question about the *host*, so a dead socket answers it rather
        // than faulting it: with no connection we cannot know, and "not_found" is what the
        // caller already treats as "no information".
        _sessionStatusTcs?.TrySetResult(SessionStatuses.NotFound);
        _loopCts?.Cancel();

        if (TryStartReconnect(ex)) return;

        // Not recoverable: fail everything in flight, exactly as before.
        _connectTcs?.TrySetException(ex);
        _inputTcs?.TrySetException(ex);
        ConnectionLost?.Invoke(ex);
    }

    /// <summary>
    /// Decides whether a drop is worth retrying and, if so, owns the retry loop.
    /// <para>Reconnect only makes sense for an established session: without a
    /// <see cref="SessionId"/> the host has nothing to resume us into, so a failed first
    /// handshake stays a plain failure (and the caller's own connect timeout still applies).
    /// A drop <i>during</i> the handshake is likewise left alone — retrying inside
    /// <see cref="EnsureConnectedAsync"/> while it is still awaiting its own connect promise
    /// would have two code paths driving the same socket.</para>
    /// </summary>
    private bool TryStartReconnect(Exception ex)
    {
        if (_disposed || string.IsNullOrEmpty(SessionId)) return false;
        // A handshake in flight owns the socket; let it fail and let its caller decide.
        if (_connectTcs is not null) return false;
        // CompareExchange, not a bool check: each attempt's failure re-enters here from the
        // receive loop, and a second loop would double the retry rate and the backoff schedule.
        if (Interlocked.CompareExchange(ref _reconnectLoopActive, 1, 0) != 0) return true;

        _ = Task.Run(() => ReconnectLoopAsync(ex));
        return true;
    }

    private async Task ReconnectLoopAsync(Exception cause)
    {
        try
        {
            for (var attempt = 1; ReconnectPolicy.ShouldRetry(attempt); attempt++)
            {
                var delay = _reconnectPolicy.DelayFor(attempt);
                Reconnecting?.Invoke(new ReconnectingEvent(attempt, delay, cause));
                await Task.Delay(delay).ConfigureAwait(false);
                if (_disposed) return;

                try
                {
                    // Tear the dead socket down first: EnsureConnectedAsync would otherwise
                    // overwrite _ws and leak the previous ClientWebSocket and its receive loop.
                    await CloseSocketAsync().ConfigureAwait(false);
                    await EnsureConnectedAsync(SessionId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cause = ex;
                    continue;
                }

                var state = LastConnectedState;
                await DrainOutboundAsync().ConfigureAwait(false);
                Reconnected?.Invoke(state ?? new ConnectedState(
                    SessionId, ConnectedStatuses.Connected, false, null, null));

                // The socket is back, but that only saves the *turn* if the host still has an
                // agent running it. On "running" the host has already rewound to our
                // last_msg_id and resumed forwarding, so the missed events — and eventually the
                // OUTPUT — arrive on the open _inputTcs and the turn simply continues. On any
                // other status nothing is going to answer it, so fail it now and let the user
                // retry rather than leaving them watching a spinner for a turn that ended.
                if (_inputTcs is { } pending && state?.IsRunning != true)
                {
                    pending.TrySetException(new InvalidOperationException(
                        "Reconnected, but the agent is no longer running this turn."));
                }
                return;
            }

            // Out of attempts. Now it is a real loss.
            var giveUp = new InvalidOperationException(
                $"Reconnect failed after {ReconnectPolicy.MaxAttempts} attempts: {cause.Message}", cause);
            _inputTcs?.TrySetException(giveUp);
            ConnectionLost?.Invoke(giveUp);
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectLoopActive, 0);
        }
    }

    /// <summary>Replays queued frames in order. A send that fails puts nothing back — the
    /// socket is down again, which re-enters the reconnect loop, and a frame that has already
    /// been half-written is not safe to repeat.</summary>
    private async Task DrainOutboundAsync()
    {
        while (_outbound.TryDequeue(out var msg))
        {
            try { await SendJsonAsync(msg, CancellationToken.None).ConfigureAwait(false); }
            catch { return; }
        }
    }

    /// <summary>
    /// Sends if the socket is up, otherwise queues for <see cref="DrainOutboundAsync"/>.
    /// <para>Used by the three human-answer frames. An agent parked on <c>ask_user</c> is
    /// blocked in <c>io.receive()</c> and stays that way across a reconnect — the host routes
    /// a late answer to the waiting agent thread — so an answer typed during an outage is
    /// still worth delivering. Dropping it would leave the agent blocked forever with no card
    /// left on screen to answer it again.</para>
    /// </summary>
    private Task SendOrQueueAsync(Dictionary<string, object?> message, CancellationToken ct)
    {
        if (IsConnected) return SendJsonAsync(message, ct);
        _outbound.Enqueue(message);
        return Task.CompletedTask;
    }

    private async Task SendJsonAsync(Dictionary<string, object?> message, CancellationToken ct)
    {
        var bytes = new ArrayBufferWriter<byte>();
        WireJson.WriteTo(bytes, message);
        await _ws!.SendAsync(bytes.WrittenMemory, WebSocketMessageType.Text, true, ct)
            .ConfigureAwait(false);
    }

    private async Task CloseSocketAsync(bool detachSubscribers = false)
    {
        _loopCts?.Cancel();
        _authenticated = false;
        _awaitingHumanResponse = false;

        using var closeCts = new CancellationTokenSource(SocketCloseTimeout);

        // Drain the receive loop so it cannot race with the socket close
        // below and spuriously call HandleConnectionLoss (which would
        // fault the pending TCS instances and re-cancel the loop CTS).
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.WaitAsync(closeCts.Token).ConfigureAwait(false); }
            catch
            {
                // A provider that does not promptly interrupt ReceiveAsync must not hold app exit.
                _ws?.Abort();
            }
            _receiveLoop = null;
        }

        // A reconnect closes only the transport; the service and its subscribers remain the same.
        // Detaching here unconditionally used to make every typed/stream event silently disappear
        // after either an automatic or user-requested reconnect. Subscribers are released only on
        // final service disposal.
        if (detachSubscribers)
        {
            StreamEvent = null;
            InputSent = null;
            ConnectionLost = null;
            Reconnecting = null;
            Reconnected = null;
            SessionDiverged = null;
            AskUserRequested = null;
            ApprovalRequested = null;
            PlanReviewRequested = null;
            OnboardRequired = null;
            ModeChanged = null;
            RuntimeInputAcknowledged = null;
        }

        if (_ws is not null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                _ws.Abort();
            }
            _ws.Dispose();
            _ws = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Before closing, so the close cannot be mistaken for a drop worth reconnecting: the
        // receive loop unwinding through HandleConnectionLoss checks this flag.
        _disposed = true;
        await CloseSocketAsync(detachSubscribers: true).ConfigureAwait(false);
        _loopCts?.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
