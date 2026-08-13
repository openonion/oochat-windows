namespace ConnectOnion.WinUIClient.Services.Notifications;

/// <summary>
/// A capacity-bounded set of recently-seen keys used to guarantee "one event →
/// one notification". Oldest keys are evicted once <see cref="_capacity"/> is
/// reached so it can never grow without bound. Not thread-safe on its own — the
/// coordinator guards access with its lock.
/// </summary>
public sealed class DedupCache
{
    private readonly int _capacity;
    private readonly HashSet<string> _set;
    private readonly Queue<string> _order;

    public DedupCache(int capacity = 256)
    {
        _capacity = capacity < 1 ? 1 : capacity;
        _set = new HashSet<string>(System.StringComparer.Ordinal);
        _order = new Queue<string>(_capacity);
    }

    /// <summary>Records <paramref name="key"/>; returns false if it was already present.</summary>
    public bool Add(string key)
    {
        if (!_set.Add(key)) return false;

        _order.Enqueue(key);
        if (_order.Count > _capacity)
        {
            var evicted = _order.Dequeue();
            _set.Remove(evicted);
        }
        return true;
    }

    public bool Contains(string key) => _set.Contains(key);
}
