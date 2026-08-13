using ConnectOnion.WinUIClient.Models.Notifications;
using ConnectOnion.WinUIClient.Services.Notifications;

namespace ConnectOnion.WinUIClient.UnitTests.Services.Notifications;

/// <summary>Settable window-state stand-in for coordinator tests.</summary>
internal sealed class FakeWindowPresence : IWindowPresence
{
    public bool IsForeground { get; set; }
    public string? ViewingConversationId { get; set; }

    public bool IsViewingConversation(string? conversationId)
        => conversationId is not null && conversationId == ViewingConversationId;

    public bool IsViewingApproval(string? conversationId)
        => IsViewingConversation(conversationId);
}

/// <summary>Records everything routed to it so tests can assert on delivery.</summary>
internal sealed class RecordingChannel : INotificationChannel
{
    public List<(NotificationRequest Request, bool PlaySound)> Shown { get; } = new();

    public void Show(NotificationRequest request, bool playSound)
        => Shown.Add((request, playSound));

    public int Count => Shown.Count;
    public NotificationRequest? Last => Shown.Count == 0 ? null : Shown[^1].Request;
}

internal sealed class FakeSettingsProvider : INotificationSettingsProvider
{
    public NotificationSettings Current { get; set; } = new();
}

/// <summary>Deterministic scheduler: callbacks fire only when a test calls
/// <see cref="FireAll"/>, and disposing a handle cancels the callback.</summary>
internal sealed class ManualScheduler : INotificationScheduler
{
    private sealed class Handle : IDisposable
    {
        public Action Callback { get; }
        public bool Cancelled { get; private set; }
        public Handle(Action callback) => Callback = callback;
        public void Dispose() => Cancelled = true;
    }

    private readonly List<Handle> _handles = new();

    public int PendingCount
    {
        get
        {
            var n = 0;
            foreach (var h in _handles) if (!h.Cancelled) n++;
            return n;
        }
    }

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var handle = new Handle(callback);
        _handles.Add(handle);
        return handle;
    }

    /// <summary>Fire every not-yet-cancelled callback once.</summary>
    public void FireAll()
    {
        foreach (var h in _handles.ToArray())
            if (!h.Cancelled) h.Callback();
    }
}
