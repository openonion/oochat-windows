namespace ConnectOnion.WinUIClient.Services.Lifecycle;

/// <summary>
/// Coalesces activation requests until the window-owned activation callback is available.
/// </summary>
/// <remarks>
/// A plain "window is null, then set a pending flag" check has a lost-wakeup race: the UI thread
/// can publish the window and consume the flag between those two operations. This gate keeps the
/// callback and pending bit under one lock, invokes callbacks outside the lock, and deliberately
/// coalesces any number of cold-start requests into one foreground operation.
/// </remarks>
public sealed class DeferredActivationGate
{
    private readonly object _gate = new();
    private Action? _activate;
    private bool _pending;

    /// <summary>
    /// Requests activation now, or remembers one request while no callback is attached.
    /// </summary>
    public void Request()
    {
        Action? activate;
        lock (_gate)
        {
            activate = _activate;
            if (activate is null)
            {
                _pending = true;
                return;
            }
        }

        activate();
    }

    /// <summary>
    /// Publishes the callback and replays one coalesced request that arrived before attachment.
    /// </summary>
    public void Attach(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);

        var replay = false;
        lock (_gate)
        {
            _activate = activate;
            replay = _pending;
            _pending = false;
        }

        if (replay) activate();
    }

    /// <summary>
    /// Stops future requests from reaching a window that is closing. A later request is retained
    /// for a future attachment rather than being silently dropped.
    /// </summary>
    public void Detach()
    {
        lock (_gate) _activate = null;
    }
}
