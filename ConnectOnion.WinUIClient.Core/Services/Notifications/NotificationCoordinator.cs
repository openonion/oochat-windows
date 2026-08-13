using ConnectOnion.WinUIClient.Models.Notifications;

namespace ConnectOnion.WinUIClient.Services.Notifications;

/// <summary>
/// The single place that decides whether and how to notify. Callers hand it
/// high-level events (a completed turn, an approval, a lost connection); it
/// de-duplicates, applies <see cref="NotificationPolicy"/>, and routes to the
/// system or in-app channel. Nothing else in the app should call the OS toast
/// APIs directly. Thread-safe and WinUI-free (channels handle their own UI
/// marshaling), so it can be driven from background socket callbacks and unit-
/// tested with fakes.
/// </summary>
public sealed class NotificationCoordinator : IDisposable
{
    private readonly INotificationChannel _system;
    private readonly INotificationChannel _inApp;
    private readonly IWindowPresence _presence;
    private readonly INotificationSettingsProvider _settings;
    private readonly INotificationScheduler _scheduler;
    private readonly NotificationPolicy _policy;
    private readonly Action<string, Exception>? _onError;
    private readonly IConversationAttentionStore? _attentionStore;

    // Guards the three collections below. Held only across their mutations — never across a
    // channel Show or a policy decision, both of which can reach into UI code and would turn
    // this into a lock ordering problem with the dispatcher.
    private readonly object _gate = new();
    private readonly DedupCache _dedup = new(256);
    /// <summary>Agent id → armed grace timer for a disconnect that has not yet been reported.
    /// Presence of a key means "counting down"; a reconnect inside the window disposes it and
    /// the user never hears about the blip.</summary>
    private readonly Dictionary<string, IDisposable> _pendingConnection = new(StringComparer.Ordinal);
    /// <summary>Agents the user has already been told are down. This — not the dedup cache — is
    /// what limits an outage to one notification, because an outage can outlive 256 other
    /// events and would otherwise re-fire once its key aged out.</summary>
    private readonly HashSet<string> _notifiedDown = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>How long a disconnect must persist before we notify. Short blips
    /// that recover within this window are silent.</summary>
    public TimeSpan ConnectionGracePeriod { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A turn is treated as a long-running "task" (not a chat reply) when
    /// it ran tools or took at least this long.</summary>
    public TimeSpan TaskDurationThreshold { get; set; } = TimeSpan.FromSeconds(12);

    public NotificationCoordinator(
        INotificationChannel systemChannel,
        INotificationChannel inAppChannel,
        IWindowPresence presence,
        INotificationSettingsProvider settings,
        INotificationScheduler scheduler,
        NotificationPolicy? policy = null,
        Action<string, Exception>? onError = null,
        IConversationAttentionStore? attentionStore = null)
    {
        _system = systemChannel;
        _inApp = inAppChannel;
        _presence = presence;
        _settings = settings;
        _scheduler = scheduler;
        _policy = policy ?? new NotificationPolicy();
        _onError = onError;
        _attentionStore = attentionStore;
    }

    /// <summary>Core entry point: de-dup, decide, and route a request.</summary>
    public void Publish(NotificationRequest request)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (!_dedup.Add(request.EffectiveDedupKey)) return;
            }

            var decision = _policy.Decide(request, _presence, _settings.Current);
            NotificationLog.Info($"Decision: channel={decision.Channel} unread={decision.MarkUnread} foreground={_presence.IsForeground} type={request.Type} conv={request.ConversationId}");

            // Runs ahead of the channel check: a suppressed toast still leaves an unread
            // conversation unless the user was looking at it. See NotificationDecision.MarkUnread.
            if (decision.MarkUnread) PersistAttentionIfNeeded(request);
            if (decision.Channel == NotificationChannel.None) return;

            // The policy may rewrite the body — it is what strips the message preview when the
            // user has previews off — so its version wins wherever it supplied one.
            var shaped = request with { Body = decision.Body ?? request.Body };
            var channel = decision.Channel == NotificationChannel.System ? _system : _inApp;
            channel.Show(shaped, decision.PlaySound);
        }
        catch (Exception ex)
        {
            // A notification failure must never bubble into the chat/save path.
            _onError?.Invoke("Publish failed", ex);
        }
    }

    private void PersistAttentionIfNeeded(NotificationRequest request)
    {
        if (_attentionStore is null || string.IsNullOrWhiteSpace(request.ConversationId)) return;
        if (request.Type is not (NotificationType.AgentReply
            or NotificationType.TaskCompleted
            or NotificationType.ApprovalRequired)) return;

        _ = PersistAttentionAsync(
            request.ConversationId,
            request.Type == NotificationType.ApprovalRequired);
    }

    private async Task PersistAttentionAsync(string conversationId, bool requiresAttention)
    {
        try
        {
            // Re-checked here, not only in the policy. This runs off the publishing thread, so the
            // user can have opened the conversation in the meantime — typically by clicking the
            // very toast this call accompanies. The clear that opening performs would then land
            // *before* this write, leaving a badge on the chat they are reading with nothing left
            // to clear it. Narrows the window rather than closing it: a genuinely simultaneous
            // interleave is still possible, and costs one stale badge until the next time the
            // window is focused, which MainWindow.ClearAttentionForVisibleConversation resolves.
            if (_presence.IsViewingConversation(conversationId)) return;

            await _attentionStore!.MarkUnreadAsync(conversationId, requiresAttention)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _onError?.Invoke("Persisting conversation attention failed", ex);
        }
    }

    /// <summary>An agent turn finished. Classified as a task (ran tools / slow) or
    /// a plain reply; dedup keyed on the turn so a turn only ever notifies once.</summary>
    public void PublishAgentTurnCompleted(
        string agentId,
        string agentName,
        string conversationId,
        string turnKey,
        string? replyText,
        long toolCallCount,
        double durationMs)
    {
        agentName = Common.FriendlyAgentName.From(agentName);
        var isTask = toolCallCount > 0 || durationMs >= TaskDurationThreshold.TotalMilliseconds;
        var type = isTask ? NotificationType.TaskCompleted : NotificationType.AgentReply;
        var preview = NotificationText.Preview(replyText);
        if (string.IsNullOrEmpty(preview))
            preview = NotificationText.GenericBody(type);

        Publish(new NotificationRequest(
            Title: agentName,
            Body: preview,
            Type: type,
            AgentId: agentId,
            ConversationId: conversationId,
            DedupKey: $"turn:{conversationId}:{turnKey}"));
    }

    /// <summary>The agent asked the user to approve a sensitive action.</summary>
    public void PublishApprovalRequired(
        string agentId,
        string agentName,
        string conversationId,
        string tool,
        string? description,
        string argumentsJson)
    {
        agentName = Common.FriendlyAgentName.From(agentName);
        var body = string.IsNullOrWhiteSpace(description)
            ? $"{agentName} wants to run \"{tool}\"."
            : NotificationText.Preview(description);

        Publish(new NotificationRequest(
            Title: "Approval needed",
            Body: body,
            Type: NotificationType.ApprovalRequired,
            AgentId: agentId,
            ConversationId: conversationId,
            ActionId: $"{conversationId}:{tool}",
            DedupKey: $"approval:{conversationId}:{tool}:{Hash(argumentsJson)}"));
    }

    /// <summary>Report an important, user-actionable error.</summary>
    public void PublishError(string agentId, string? conversationId, string title, string message)
        => Publish(new NotificationRequest(
            Title: title,
            Body: NotificationText.Preview(message),
            Type: NotificationType.Error,
            AgentId: agentId,
            ConversationId: conversationId,
            DedupKey: $"error:{agentId}:{title}:{message}"));

    /// <summary>A connection dropped. Notifies only if it stays down past the grace
    /// period; a recovery (<see cref="NotifyConnectionRestored"/>) cancels it.</summary>
    public void NotifyConnectionLost(string agentId, string agentName)
    {
        if (string.IsNullOrEmpty(agentId)) return;
        agentName = Common.FriendlyAgentName.From(agentName);
        lock (_gate)
        {
            if (_disposed) return;
            if (_notifiedDown.Contains(agentId)) return;       // already told the user
            if (_pendingConnection.ContainsKey(agentId)) return; // grace timer already armed
            _pendingConnection[agentId] = _scheduler.Schedule(
                ConnectionGracePeriod, () => OnGraceElapsed(agentId, agentName));
        }
    }

    /// <summary>The connection came back (or a turn succeeded): cancel any pending
    /// grace timer and re-arm for future disconnects.</summary>
    public void NotifyConnectionRestored(string agentId)
    {
        if (string.IsNullOrEmpty(agentId)) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (_pendingConnection.Remove(agentId, out var handle)) handle.Dispose();
            _notifiedDown.Remove(agentId);
        }
    }

    /// <summary>The grace timer fired without a reconnect, so the outage is real. Both checks
    /// below are races the scheduler can lose: a reconnect that landed while the callback was
    /// already queued clears the pending entry, and a duplicate timer would find the agent
    /// already marked down.</summary>
    private void OnGraceElapsed(string agentId, string agentName)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_pendingConnection.Remove(agentId, out var handle)) return;
            handle.Dispose();
            if (!_notifiedDown.Add(agentId)) return;
        }

        Publish(new NotificationRequest(
            Title: "Connection lost",
            Body: $"{agentName} is offline. Trying to reconnect…",
            Type: NotificationType.ConnectionLost,
            AgentId: agentId,
            // Deliberately unique: dedup must not swallow this one. Suppression for repeat
            // outages is _notifiedDown's job above, and it has already run by the time we
            // get here — a stable key would only add a second, weaker filter on top.
            DedupKey: $"conn:{agentId}:{Guid.NewGuid():N}"));
    }

    /// <summary>Folds a tool's arguments into the approval dedup key, so re-asking for the same
    /// tool with *different* arguments still notifies. Only ever compared against keys from the
    /// same run of the app, which is what makes .NET's per-process-randomized string hash
    /// acceptable here — do not persist or transmit this value.</summary>
    private static string Hash(string? s)
        => string.IsNullOrEmpty(s) ? "0" : ((uint)s.GetHashCode()).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose()
    {
        IDisposable[] handles;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            handles = _pendingConnection.Values.ToArray();
            _pendingConnection.Clear();
            _notifiedDown.Clear();
        }

        foreach (var handle in handles) handle.Dispose();
    }
}
