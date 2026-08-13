namespace ConnectOnion.WinUIClient.Services.Notifications;

/// <summary>
/// Tracks the single app window's focus/visibility and which conversation it is
/// showing, so the policy can tell "is the user looking at this conversation right
/// now". The app is single-window, so this holds one slot rather than a window
/// collection; the <see cref="IChatWindow"/> parameters are kept on the public API
/// for source compatibility and validated by reference. Implements the pure
/// <see cref="IWindowPresence"/> the policy depends on, plus concrete lookups the
/// navigation/in-app layers use.
/// </summary>
public sealed class WindowPresenceService : IWindowPresence
{
    private sealed class State
    {
        public bool Active;
        public bool Visible = true;
        public bool Obscured;
        public string? AgentId;
        public string? ConversationId;
    }

    private IChatWindow? _window;
    private State _state = new();
    private readonly object _gate = new();

    public void Register(IChatWindow window)
    {
        lock (_gate) { _window = window; _state = new State(); }
    }

    public void Unregister(IChatWindow window)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
                _state = new State();
            }
        }
    }

    public void SetActive(IChatWindow window, bool active)
    {
        lock (_gate) { if (ReferenceEquals(_window, window)) _state.Active = active; }
    }

    public void SetVisible(IChatWindow window, bool visible)
    {
        lock (_gate) { if (ReferenceEquals(_window, window)) _state.Visible = visible; }
    }

    /// <summary>Reports which conversation the window is showing (null when it isn't on a chat).</summary>
    public void SetViewing(IChatWindow window, string? agentId, string? conversationId)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_window, window)) return;
            _state.AgentId = agentId;
            _state.ConversationId = conversationId;
        }
    }

    /// <summary>
    /// Reports that a full-window modal is covering the content area.
    ///
    /// <para>The shell's overlays (Settings, About, shortcuts, Add Agent, session search) are
    /// <c>UserControl</c>s in a floating layer, not navigations — the <c>ChatPage</c> underneath
    /// stays loaded and keeps reporting itself as the conversation on screen. Without this the
    /// policy concluded the user was reading a chat they could not see, and a reply that landed
    /// while Settings was open produced no toast <i>and</i> no unread badge.</para>
    /// </summary>
    public void SetContentObscured(IChatWindow window, bool obscured)
    {
        lock (_gate) { if (ReferenceEquals(_window, window)) _state.Obscured = obscured; }
    }

    /// <summary>The conversation the window is showing to the user right now, or null when it is
    /// on another page or a modal is covering it. This is the "are they already reading it"
    /// question — distinct from <see cref="FindViewing"/>, which asks which window <i>hosts</i> a
    /// conversation so a clicked notification can be routed there.</summary>
    public string? VisibleConversationId
    {
        get
        {
            lock (_gate)
                return _window is not null && _state.Active && _state.Visible && !_state.Obscured
                    ? _state.ConversationId
                    : null;
        }
    }

    public bool IsForeground
    {
        get { lock (_gate) return _window is not null && _state.Active && _state.Visible; }
    }

    public bool IsViewingConversation(string? conversationId)
    {
        if (string.IsNullOrEmpty(conversationId)) return false;
        lock (_gate)
            return _window is not null && _state.Active && _state.Visible && !_state.Obscured
                && _state.ConversationId == conversationId;
    }

    public bool IsViewingApproval(string? conversationId) => IsViewingConversation(conversationId);

    /// <summary>The window itself if it is showing the conversation, or null.</summary>
    public IChatWindow? FindViewing(string conversationId)
    {
        lock (_gate)
            return _window is not null && _state.ConversationId == conversationId ? _window : null;
    }

    /// <summary>The window to route into: the single registered window, or null when
    /// none exists. (Previously picked the best of several; with one window this is it.)</summary>
    public IChatWindow? ForegroundWindow()
    {
        lock (_gate) return _window;
    }
}
