using System;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectOnion.Protocol.Runtime;

/// <summary>
/// The live, mutable state of one agent turn. Owned exclusively by
/// <see cref="ConversationRunRegistry"/>; callers only ever see immutable
/// <see cref="ConversationRunSnapshot"/> projections of it. All mutation goes through
/// the registry while holding <see cref="Gate"/>, so a background executor thread and a
/// UI-driven cancel/subscribe can never corrupt the buffer or double-finalize.
/// </summary>
internal sealed class ConversationRun
{
    public required TurnRequest Request { get; init; }
    public required CancellationTokenSource Cts { get; init; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Single lock guarding every field below and the finalize-once invariant.</summary>
    public object Gate { get; } = new();

    public ConversationRunStatus Status { get; set; } = ConversationRunStatus.Queued;
    public string PartialContent { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public long Sequence { get; set; }
    public bool Finalized { get; set; }

    // Append-only event buffer. A snapshot exposes it as an ArraySegment over the array as it
    // stands right now, rather than a fresh copy: the previous shape (a List plus Events.ToArray()
    // on every publish) copied the whole buffer once per event, so a turn with E events allocated
    // O(E^2) references' worth of arrays purely to hand each subscriber a list it only reads the
    // tail of. Appending is safe for already-published snapshots because we only ever write at
    // index >= their Count, and growing swaps in a *new* array, leaving theirs untouched.
    private AgentStreamEvent[] _events = Array.Empty<AgentStreamEvent>();
    private int _eventCount;

    /// <summary>Appends an event. Caller must hold <see cref="Gate"/>.</summary>
    public void AddEventLocked(AgentStreamEvent streamEvent)
    {
        if (_eventCount == _events.Length)
        {
            var grown = new AgentStreamEvent[_events.Length == 0 ? 8 : _events.Length * 2];
            Array.Copy(_events, grown, _eventCount);
            _events = grown;
        }
        _events[_eventCount++] = streamEvent;
    }

    /// <summary>The background turn task; observed by the registry (never fire-and-forget-unobserved).</summary>
    public Task? ExecutionTask { get; set; }

    public string RunId => Request.RunId;
    public string ConversationId => Request.ConversationId;
    public string AgentId => Request.AgentId;

    /// <summary>Builds an immutable snapshot. Caller must hold <see cref="Gate"/>.</summary>
    public ConversationRunSnapshot SnapshotLocked(ConversationRunStatus? statusOverride = null) => new(
        RunId,
        ConversationId,
        AgentId,
        Request.UserMessageId,
        Request.AssistantMessageId,
        statusOverride ?? Status,
        PartialContent,
        Sequence,
        ErrorCode,
        ErrorMessage,
        new ArraySegment<AgentStreamEvent>(_events, 0, _eventCount));
}
