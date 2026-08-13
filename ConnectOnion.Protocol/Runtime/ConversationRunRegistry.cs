using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectOnion.Protocol.Runtime;

/// <summary>
/// App-level, thread-safe owner of every in-flight agent turn. This is the single
/// source of truth for "what is streaming right now"; pages and view models only
/// subscribe to it. A run keeps executing (and persists its reply) after the page that
/// started it is gone, so switching sessions, minimizing to the tray, or reopening the
/// conversation never interrupts or loses a reply.
///
/// Design invariants:
/// <list type="bullet">
/// <item>At most one active run per conversation; different conversations run in parallel.</item>
/// <item>Every state transition is published as an immutable snapshot with a monotonic sequence.</item>
/// <item>A terminal state (Completed/Failed/Cancelled) is set exactly once.</item>
/// <item>On success the reply is persisted <b>before</b> the run is marked Completed.</item>
/// <item>No network/DB/UI work happens while holding a run's lock.</item>
/// </list>
/// </summary>
/// <summary>Error codes a run can end with that callers branch on.</summary>
public static class RunErrorCodes
{
    /// <summary>The user stopped the turn. It is over and nothing is coming.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// The app closed while the turn was running. Crucially <b>not</b> the same as
    /// <see cref="Cancelled"/>: nothing told the host to stop, so its agent is very likely still
    /// executing — possibly blocked on an approval nobody is there to answer. Persistence
    /// therefore leaves such a turn's <c>executions</c> row open so the next open can rejoin it,
    /// rather than writing a half-turn to history and settling it.
    /// </summary>
    public const string Shutdown = "shutdown";
}

public sealed class ConversationRunRegistry
{
    // A terminal snapshot is only a short hand-off aid for a page that opens just after a run
    // completes. Durable history already lives in SQLite, so retaining one entry for every
    // conversation ever used would turn ordinary long-term use into process-wide growth.
    private const int MaxTerminalSnapshots = 64;
    /// <summary>How long a failed turn stays retryable. Long enough that a user who steps away
    /// mid-failure still finds the button live when they come back; short enough that an
    /// abandoned failed turn's attachments do not sit in memory all day. Expiry is lazy — there
    /// is no timer; the map is swept whenever it is written to or read.</summary>
    private static readonly TimeSpan RetryableTtl = TimeSpan.FromMinutes(30);

    private readonly IRunPersistence _persistence;
    private readonly IRunLogger _logger;
    private readonly Action<Exception>? _onError;
    private readonly Func<DateTimeOffset> _clock;

    // All four are keyed by conversation id and outlive any page.
    /// <summary>Runs currently executing. An entry existing here *is* the "conversation is
    /// busy" flag, which is why entries are removed only once the run is fully terminal.</summary>
    private readonly ConcurrentDictionary<string, ConversationRun> _active = new();
    /// <summary>The last snapshot per conversation, kept after the run leaves <c>_active</c> so
    /// a page opened moments later can still see how the turn ended instead of finding nothing.</summary>
    private readonly ConcurrentDictionary<string, ConversationRunSnapshot> _lastSnapshots = new();
    private readonly ConcurrentQueue<(string ConversationId, string RunId)> _terminalSnapshotOrder = new();
    /// <summary>Requests from failed runs, held so the UI can offer a retry without the caller
    /// having to reconstruct the original turn. Cleared when a new run starts, and expired out
    /// after <see cref="RetryableTtl"/> — a <see cref="TurnRequest"/> carries the turn's images
    /// and files as base64 data URLs, so an entry that is never retried would otherwise pin the
    /// whole payload for the life of the process.</summary>
    private readonly ConcurrentDictionary<string, RetryableRequest> _retryable = new();
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();

    // volatile: set on the shutdown path and read by StartRun on arbitrary threads, with no
    // lock between them. Only ever goes false → true, so a plain flag is sufficient.
    private volatile bool _shuttingDown;

    public ConversationRunRegistry(
        IRunPersistence persistence,
        IRunLogger? logger = null,
        Action<Exception>? onError = null,
        Func<DateTimeOffset>? clock = null)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _logger = logger ?? NullRunLogger.Instance;
        _onError = onError;
        // Injectable purely so the retry-expiry test does not have to wait out a real TTL.
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Registers and starts a run for <paramref name="request"/>. Throws
    /// <see cref="InvalidOperationException"/> if the conversation already has an active
    /// run (the caller should gate its send UI on <see cref="GetActiveRun"/>).
    /// </summary>
    public ConversationRunSnapshot StartRun(TurnRequest request, ITurnExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(executor);
        if (_shuttingDown) throw new InvalidOperationException("The registry is shutting down.");

        var run = new ConversationRun
        {
            Request = request,
            Cts = new CancellationTokenSource(),
        };

        // TryAdd is the enforcement point for "one run per conversation" — an atomic claim
        // rather than a check-then-add, so two simultaneous sends cannot both win.
        if (!_active.TryAdd(request.ConversationId, run))
        {
            run.Cts.Dispose();
            throw new InvalidOperationException(
                $"Conversation '{request.ConversationId}' already has an active run.");
        }

        _retryable.TryRemove(request.ConversationId, out _);

        ConversationRunSnapshot snapshot;
        lock (run.Gate)
        {
            run.Sequence++;
            snapshot = run.SnapshotLocked();
        }
        // Published before the task starts, so a caller that subscribes on the next line is
        // guaranteed to see the run exist rather than racing its first event.
        StoreAndPublish(snapshot, "Queued", run.CreatedAt);

        // Task.Run, not a bare call: StartRun is invoked from the UI thread, and the executor's
        // synchronous prologue would otherwise run there. The task is deliberately not awaited —
        // outliving its caller is the entire point of this class.
        run.ExecutionTask = Task.Run(() => RunAsync(run, executor));
        return snapshot;
    }

    /// <summary>The active (non-terminal) run for a conversation, or the last terminal
    /// snapshot if one recently finished, or null. Used by a page opening a conversation
    /// to decide whether to resume live streaming.</summary>
    public ConversationRunSnapshot? GetActiveRun(string conversationId)
    {
        if (_active.TryGetValue(conversationId, out var run))
        {
            lock (run.Gate) return run.SnapshotLocked();
        }
        return _lastSnapshots.TryGetValue(conversationId, out var last) ? last : null;
    }

    /// <summary>Whether a run is <i>currently executing</i> for this conversation.
    /// <para>Deliberately distinct from <see cref="GetActiveRun"/>, which falls back to the last
    /// terminal snapshot and therefore keeps returning non-null forever once a conversation has
    /// run even once. Callers asking "is this conversation busy right now?" — resource reclamation
    /// above all — must use this; a caller that wants "what is the current or most recent run"
    /// wants <see cref="GetActiveRun"/> and its fallback.</para></summary>
    public bool IsRunActive(string conversationId) => _active.ContainsKey(conversationId);

    /// <summary>Snapshots of every currently-executing run across all conversations.</summary>
    public IReadOnlyList<ConversationRunSnapshot> GetActiveRuns()
    {
        var list = new List<ConversationRunSnapshot>();
        foreach (var run in _active.Values)
        {
            lock (run.Gate) list.Add(run.SnapshotLocked());
        }
        return list;
    }

    /// <summary>
    /// Subscribes to run updates for one conversation. The handler is invoked immediately
    /// with the current snapshot (if any) so a page created mid-turn resumes instantly,
    /// then on every subsequent change. Dispose the result to detach — this never cancels
    /// the underlying run, so closing a page/window leaves the turn running.
    /// </summary>
    public IDisposable Subscribe(string conversationId, Action<ConversationRunSnapshot> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = _subscriptions.GetOrAdd(conversationId, _ => new Subscription());
        var token = subscription.Add(handler);

        // Replay-on-subscribe. This is what lets a page attach mid-turn and catch up: it gets
        // the accumulated snapshot immediately, then live updates. Note the ordering — the
        // handler is registered *before* the replay, so an update landing in between is
        // delivered rather than dropped (the cost is a possible duplicate, which the snapshot's
        // monotonic sequence lets consumers discard).
        var current = GetActiveRun(conversationId);
        if (current is not null) SafeInvoke(handler, current);

        return new Unsubscriber(subscription, token);
    }

    /// <summary>Cancels the active run if its id matches, and waits (bounded) for it to
    /// unwind to a terminal state. A no-op if the run already finished.</summary>
    public async Task CancelRunAsync(string conversationId, string runId)
    {
        if (!_active.TryGetValue(conversationId, out var run) || run.RunId != runId) return;

        run.Cts.Cancel();
        var task = run.ExecutionTask;
        if (task is not null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch { /* bounded wait; RunAsync's own finalize is authoritative */ }
        }
    }

    /// <summary>
    /// Appends a client-originated lifecycle marker to the active run's buffer. The stop marker
    /// lets every page and the persistence projection associate a later ordinary OUTPUT with the
    /// INTERRUPT that caused it, without inventing a backend frame type.
    /// Returns false if the run already finished, in which case there is nothing to annotate.
    /// </summary>
    public bool PublishLocalEvent(string conversationId, AgentStreamEvent streamEvent)
    {
        if (!_active.TryGetValue(conversationId, out var run)) return false;
        PublishEvent(run, streamEvent);
        return true;
    }

    /// <summary>
    /// Starts a fresh run for a conversation whose last run failed, reusing the original
    /// user message but minting a new RunId and AssistantMessageId. Returns null if there
    /// is nothing retryable (e.g. the last run succeeded, or a run is already active).
    /// </summary>
    public ConversationRunSnapshot? RetryRun(string conversationId, ITurnExecutor executor)
    {
        PruneExpiredRetryables();
        if (!_retryable.TryGetValue(conversationId, out var entry)) return null;
        if (_active.ContainsKey(conversationId)) return null;

        var retry = entry.Request with
        {
            RunId = Guid.NewGuid().ToString(),
            AssistantMessageId = Guid.NewGuid().ToString(),
        };
        return StartRun(retry, executor);
    }

    /// <summary>
    /// Drops everything this registry remembers about a conversation — its last snapshot, its
    /// retryable request, and its subscription slot. Call when the conversation is deleted.
    /// <para>Without this the three maps are keyed by conversation id and never shrink: a user
    /// who works through and deletes many conversations leaves behind one terminal snapshot
    /// (holding the full reply text) each, for the life of the process.</para>
    /// <para>Does <b>not</b> cancel an active run — cancellation is the caller's decision and it
    /// has its own bounded-wait path (<see cref="CancelRunAsync"/>). Forgetting a conversation
    /// whose run is still executing is harmless: the run finishes and re-populates its snapshot,
    /// which is then simply never read.</para>
    /// </summary>
    public void Forget(string conversationId)
    {
        _lastSnapshots.TryRemove(conversationId, out _);
        _retryable.TryRemove(conversationId, out _);
        _subscriptions.TryRemove(conversationId, out _);
    }

    /// <summary>
    /// Real app shutdown: stop accepting new runs, cancel everything in flight, and wait a
    /// bounded time for each to persist recoverable state and release its transport. Never
    /// blocks forever. Safe to call once.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        // Order matters: the flag goes up first so no new run can be registered into the set we
        // are about to snapshot and cancel.
        _shuttingDown = true;
        var runs = _active.Values.ToArray();
        foreach (var run in runs) run.Cts.Cancel();

        var tasks = runs.Select(r => r.ExecutionTask).Where(t => t is not null).Cast<Task>().ToArray();
        if (tasks.Length == 0) return;

        try { await Task.WhenAll(tasks).WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            var stragglers = _active.Values.Select(r => r.RunId);
            _onError?.Invoke(new TimeoutException(
                $"Shutdown timed out with runs still finalizing: {string.Join(", ", stragglers)}"));
        }
        catch (Exception ex) { _onError?.Invoke(ex); }
    }

    // ----- the background turn -----

    private async Task RunAsync(ConversationRun run, ITurnExecutor executor)
    {
        var sink = new Sink(this, run);
        try
        {
            run.StartedAt = DateTimeOffset.UtcNow;
            var finalReply = await executor.ExecuteAsync(run.Request, sink).ConfigureAwait(false);

            // Persist BEFORE flipping to Completed: a subscriber that reacts to Completed
            // by reloading the DB must find the reply already committed.
            var pending = SnapshotFor(run, ConversationRunStatus.Completed);
            await _persistence.PersistCompletedAsync(pending, finalReply).ConfigureAwait(false);

            Finalize(run, ConversationRunStatus.Completed, finalReply, null, null);
        }
        // The `when` filter matters: only a cancellation *we* requested counts as Cancelled. An
        // OperationCanceledException from anywhere else (an HTTP timeout inside the executor) is
        // a genuine failure and must fall through to the Failed handler, where it becomes
        // retryable — reporting it as a user cancellation would hide a real problem.
        catch (OperationCanceledException) when (run.Cts.IsCancellationRequested)
        {
            // A cancellation the *app shutdown* caused is reported under its own code, because
            // it means something completely different from a user pressing Stop: the agent on
            // the host is still running this turn, and only the client went away. Persistence
            // uses the distinction to leave the turn resumable instead of settling it — see
            // RunErrorCodes.Shutdown.
            var (code, message) = _shuttingDown
                ? (RunErrorCodes.Shutdown, "The app closed while this turn was running.")
                : (RunErrorCodes.Cancelled, "The turn was cancelled.");
            await FinalizeFailureAsync(run, ConversationRunStatus.Cancelled, code, message);
        }
        catch (Exception ex)
        {
            await FinalizeFailureAsync(run, ConversationRunStatus.Failed, ex.GetType().Name, ex.Message);
        }
        finally
        {
            // Key-and-value overload, not TryRemove(key): by the time this runs a *newer* run
            // may already hold this conversation's slot, and removing by key alone would evict
            // it — leaving a live turn invisible to GetActiveRun and the conversation wrongly
            // idle. Only remove the entry if it is still this run.
            _active.TryRemove(new KeyValuePair<string, ConversationRun>(run.ConversationId, run));
            run.Cts.Dispose();
        }
    }

    private async Task FinalizeFailureAsync(
        ConversationRun run, ConversationRunStatus status, string code, string message)
    {
        // Persist the partial/error snapshot best-effort so a crash-then-reopen still shows
        // what was generated; never let a persistence failure mask the terminal transition.
        try
        {
            var pending = SnapshotFor(run, status, code, message);
            await _persistence.PersistFailedAsync(pending).ConfigureAwait(false);
        }
        catch (Exception ex) { _onError?.Invoke(ex); }

        Finalize(run, status, null, code, message);
        if (status == ConversationRunStatus.Failed)
        {
            _retryable[run.ConversationId] = new RetryableRequest(run.Request, _clock());
            // Sweep here rather than on a timer: failures are rare and this map is tiny, so the
            // scan is free, and it means an app that fails once and then sits idle still lets go
            // of every *other* conversation's stale payload.
            PruneExpiredRetryables();
        }
    }

    /// <summary>Drops retryable requests past their TTL. Cheap enough to run on every write and
    /// every retry attempt — the map holds at most one entry per conversation that has failed.</summary>
    private void PruneExpiredRetryables()
    {
        var now = _clock();
        foreach (var (conversationId, entry) in _retryable)
        {
            if (now - entry.StoredAt < RetryableTtl) continue;
            // Key-and-value overload so a fresher failure recorded since this loop started is
            // not evicted by an expiry decision made about the entry it replaced.
            _retryable.TryRemove(new KeyValuePair<string, RetryableRequest>(conversationId, entry));
        }
    }

    private sealed record RetryableRequest(TurnRequest Request, DateTimeOffset StoredAt);

    private ConversationRunSnapshot SnapshotFor(
        ConversationRun run, ConversationRunStatus status, string? code = null, string? message = null)
    {
        lock (run.Gate)
        {
            if (code is not null) run.ErrorCode = code;
            if (message is not null) run.ErrorMessage = message;
            return run.SnapshotLocked(status);
        }
    }

    /// <summary>The one place a run becomes terminal. Every other mutation path checks
    /// <c>Finalized</c> and bails, so once this has run no later event can revive the run or
    /// overwrite how it ended — which is what makes "terminal exactly once" hold even when a
    /// cancellation and a completion arrive at the same moment.</summary>
    private void Finalize(
        ConversationRun run, ConversationRunStatus status,
        string? finalReply, string? code, string? message)
    {
        ConversationRunSnapshot snapshot;
        lock (run.Gate)
        {
            if (run.Finalized) return; // terminal exactly once
            run.Finalized = true;
            run.Status = status;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.ErrorCode = code;
            run.ErrorMessage = message;
            // An empty final reply leaves the streamed partial in place rather than blanking it:
            // some turns answer entirely through the stream and send nothing in the OUTPUT.
            if (finalReply is not null && finalReply.Length > 0) run.PartialContent = finalReply;
            run.Sequence++;
            snapshot = run.SnapshotLocked();
        }
        // Publishing happens outside the lock — subscribers run arbitrary code (including UI
        // marshalling), and holding a run's gate across that is how deadlocks get built.
        var elapsed = (run.CompletedAt ?? DateTimeOffset.UtcNow) - run.CreatedAt;
        StoreAndPublish(snapshot, status.ToString(), run.CreatedAt, elapsed);
    }

    // ----- sink callbacks (from the executor thread) -----
    //
    // All three follow the same shape: take the gate, bail if already terminal or unchanged,
    // mutate, bump Sequence, snapshot, release, then publish. The no-op guards are what keep a
    // chatty executor from waking every subscriber with an identical snapshot.

    private void SetStatus(ConversationRun run, ConversationRunStatus status)
    {
        ConversationRunSnapshot snapshot;
        lock (run.Gate)
        {
            if (run.Finalized || run.Status == status) return;
            run.Status = status;
            run.Sequence++;
            snapshot = run.SnapshotLocked();
        }
        StoreAndPublish(snapshot, status.ToString(), run.CreatedAt);
    }

    private void PublishEvent(ConversationRun run, AgentStreamEvent streamEvent)
    {
        ConversationRunSnapshot snapshot;
        lock (run.Gate)
        {
            if (run.Finalized) return;
            run.AddEventLocked(streamEvent);
            run.Sequence++;
            snapshot = run.SnapshotLocked();
        }
        StoreAndPublish(snapshot, "Streaming", run.CreatedAt);
    }

    private void UpdatePartial(ConversationRun run, string partial)
    {
        ConversationRunSnapshot snapshot;
        lock (run.Gate)
        {
            if (run.Finalized || run.PartialContent == partial) return;
            run.PartialContent = partial;
            run.Sequence++;
            snapshot = run.SnapshotLocked();
        }
        StoreAndPublish(snapshot, "Streaming", run.CreatedAt);
    }

    // ----- publish / subscriber fan-out -----

    private void StoreAndPublish(
        ConversationRunSnapshot snapshot, string phase, DateTimeOffset createdAt, TimeSpan? elapsed = null)
    {
        // Subscribers get the full snapshot (a live page still has this run's tail to project),
        // but what we *retain* for a finished run drops the events. They have already been
        // projected into bubbles and written to trace_events; keeping them here pinned the whole
        // turn's raw JSON — every LLM call's message history, every base64 image — in memory for
        // the rest of the process's life, once per conversation. Nothing reads the events off a
        // terminal snapshot: GetActiveRun's callers only look at RunId/Status/ErrorMessage.
        if (snapshot.IsTerminal)
        {
            // Raw events are durable by this point. PartialContent stays because a page that
            // attaches in the small completion hand-off window still uses it to finish its live
            // projection; the capacity below bounds how many such replies remain resident.
            var retained = snapshot with
            {
                Events = Array.Empty<AgentStreamEvent>(),
            };
            _lastSnapshots[snapshot.ConversationId] = retained;
            _terminalSnapshotOrder.Enqueue((snapshot.ConversationId, snapshot.RunId));

            while (_terminalSnapshotOrder.Count > MaxTerminalSnapshots
                && _terminalSnapshotOrder.TryDequeue(out var oldest)
                && _lastSnapshots.TryGetValue(oldest.ConversationId, out var candidate)
                && candidate.IsTerminal
                && candidate.RunId == oldest.RunId)
            {
                _lastSnapshots.TryRemove(
                    new KeyValuePair<string, ConversationRunSnapshot>(oldest.ConversationId, candidate));
            }
        }
        else
        {
            _lastSnapshots[snapshot.ConversationId] = snapshot;
        }

        try { _logger.Log(snapshot, phase, elapsed ?? (DateTimeOffset.UtcNow - createdAt)); }
        catch (Exception ex) { _onError?.Invoke(ex); }

        if (_subscriptions.TryGetValue(snapshot.ConversationId, out var subscription))
        {
            // Safe to iterate while subscribers come and go: Handlers() walks a copy-on-write
            // array that Add/Remove replace wholesale rather than mutate. A page unsubscribing
            // from inside its own callback (what navigation does) just misses the next round.
            foreach (var handler in subscription.Handlers()) SafeInvoke(handler, snapshot);
        }
    }

    private void SafeInvoke(Action<ConversationRunSnapshot> handler, ConversationRunSnapshot snapshot)
    {
        // One subscriber throwing must not starve the others or break the run.
        try { handler(snapshot); }
        catch (Exception ex) { _onError?.Invoke(ex); }
    }

    private sealed class Sink : IRunSink
    {
        private readonly ConversationRunRegistry _registry;
        private readonly ConversationRun _run;
        public Sink(ConversationRunRegistry registry, ConversationRun run) { _registry = registry; _run = run; }

        public CancellationToken CancellationToken => _run.Cts.Token;
        public void SetConnecting() => _registry.SetStatus(_run, ConversationRunStatus.Connecting);
        public void SetRunning() => _registry.SetStatus(_run, ConversationRunStatus.Running);
        public void Publish(AgentStreamEvent streamEvent) => _registry.PublishEvent(_run, streamEvent);
        public void UpdatePartial(string partialContent) => _registry.UpdatePartial(_run, partialContent);
    }

    // Copy-on-write handler set so fan-out never holds a lock while invoking UI callbacks.
    private sealed class Subscription
    {
        private readonly object _gate = new();
        private ImmutableEntry[] _entries = Array.Empty<ImmutableEntry>();

        public long Add(Action<ConversationRunSnapshot> handler)
        {
            lock (_gate)
            {
                var token = _next++;
                _entries = _entries.Append(new ImmutableEntry(token, handler)).ToArray();
                return token;
            }
        }

        public void Remove(long token)
        {
            lock (_gate)
            {
                _entries = _entries.Where(e => e.Token != token).ToArray();
            }
        }

        public IEnumerable<Action<ConversationRunSnapshot>> Handlers()
        {
            var snapshot = _entries; // volatile-enough: reference read of an immutable array
            foreach (var e in snapshot) yield return e.Handler;
        }

        private long _next;
        private readonly record struct ImmutableEntry(long Token, Action<ConversationRunSnapshot> Handler);
    }

    private sealed class Unsubscriber : IDisposable
    {
        private Subscription? _subscription;
        private readonly long _token;
        public Unsubscriber(Subscription subscription, long token) { _subscription = subscription; _token = token; }
        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _subscription, null);
            s?.Remove(_token);
        }
    }
}
