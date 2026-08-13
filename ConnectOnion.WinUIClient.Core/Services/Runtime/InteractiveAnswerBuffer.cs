using System.Collections.Concurrent;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>
/// Orders interactive replies independently of UI-continuation timing. A reply is reserved before
/// its WebSocket write, then confirmed or cancelled when that write completes. Persistence drains
/// only confirmed replies, but waits briefly for an in-flight reservation so a very fast OUTPUT
/// cannot overtake the UI thread and turn a real answer into "Skipped".
/// </summary>
public sealed class InteractiveAnswerBuffer
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Reservation>> _conversations = new();
    private readonly ConcurrentDictionary<Guid, Reservation> _reservations = new();

    public Guid Begin(string conversationId, string meta, EventStatus status)
    {
        var reservation = new Reservation(Guid.NewGuid(), meta, status);
        _reservations[reservation.Id] = reservation;
        _conversations.GetOrAdd(conversationId, _ => new()).Enqueue(reservation);
        return reservation.Id;
    }

    public void Confirm(Guid id)
    {
        if (_reservations.TryGetValue(id, out var reservation))
            reservation.Delivery.TrySetResult(true);
    }

    public void Cancel(Guid id)
    {
        if (_reservations.TryGetValue(id, out var reservation))
            reservation.Delivery.TrySetResult(false);
    }

    public void RecordConfirmed(string conversationId, string meta, EventStatus status)
    {
        var id = Begin(conversationId, meta, status);
        Confirm(id);
    }

    public void Reset(string conversationId)
    {
        if (!_conversations.TryRemove(conversationId, out var queue)) return;
        while (queue.TryDequeue(out var reservation))
        {
            reservation.Delivery.TrySetResult(false);
            _reservations.TryRemove(reservation.Id, out _);
        }
    }

    public async Task<IReadOnlyList<RecordedInteractiveAnswer>> DrainAsync(
        string conversationId, TimeSpan? confirmationTimeout = null)
    {
        if (!_conversations.TryRemove(conversationId, out var queue))
            return Array.Empty<RecordedInteractiveAnswer>();

        var timeout = confirmationTimeout ?? TimeSpan.FromSeconds(5);
        var answers = new List<RecordedInteractiveAnswer>();
        while (queue.TryDequeue(out var reservation))
        {
            var delivered = false;
            try { delivered = await reservation.Delivery.Task.WaitAsync(timeout).ConfigureAwait(false); }
            catch (TimeoutException) { reservation.Delivery.TrySetResult(false); }
            finally { _reservations.TryRemove(reservation.Id, out _); }

            if (delivered)
                answers.Add(new RecordedInteractiveAnswer(reservation.Meta, reservation.Status));
        }
        return answers;
    }

    private sealed record Reservation(Guid Id, string Meta, EventStatus Status)
    {
        public TaskCompletionSource<bool> Delivery { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public readonly record struct RecordedInteractiveAnswer(string Meta, EventStatus Status);
