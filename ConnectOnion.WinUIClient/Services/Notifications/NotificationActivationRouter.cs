using System;
using System.Collections.Generic;
using Microsoft.Windows.AppNotifications;

namespace ConnectOnion.WinUIClient.Services.Notifications;

/// <summary>
/// Registers for App Notification activation and turns a clicked toast's
/// arguments into a navigation. Buffers a click that arrives before the UI is
/// ready (cold start) and replays it once <see cref="MarkReady"/> is called.
/// </summary>
public sealed class NotificationActivationRouter
{
    private readonly ConversationNavigationService _navigation;
    private readonly object _gate = new();
    private (string? AgentId, string? ConversationId)? _pending;
    private bool _ready;
    private int _registered;

    public NotificationActivationRouter(ConversationNavigationService navigation)
        => _navigation = navigation;

    /// <summary>Registers the app for notifications and subscribes to activation.
    /// Safe to call once at startup for both packaged and unpackaged apps.</summary>
    public void RegisterAndListen()
    {
        if (!AppNotificationCapability.IsAvailable) return;
        if (System.Threading.Interlocked.CompareExchange(ref _registered, 1, 0) != 0) return;
        AppNotificationManager? manager = null;
        try
        {
            // Default itself can throw when an unpackaged process has no permission to register
            // with the notification platform. Notifications are optional; that environment must
            // not prevent the chat window from starting.
            manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            NotificationLog.Info("RegisterAndListen: registered for notification activation");
        }
        catch (Exception ex)
        {
            if (manager is not null)
                manager.NotificationInvoked -= OnNotificationInvoked;
            System.Threading.Interlocked.Exchange(ref _registered, 0);
            AppNotificationCapability.MarkRuntimeUnavailable("registration", ex);
        }
    }

    public void Unregister()
    {
        if (System.Threading.Interlocked.Exchange(ref _registered, 0) == 0) return;
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked -= OnNotificationInvoked;
            manager.Unregister();
        }
        catch (Exception ex) { NotificationLog.Warn("notification unregister failed", ex); }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        NotificationLog.Info("NotificationInvoked (running-process COM activation)");
        try { HandleArguments(args.Arguments); }
        catch (Exception ex) { NotificationLog.Warn("activation handling failed", ex); }
    }

    /// <summary>Handles a cold-start activation (from <c>AppInstance.GetActivatedEventArgs</c>).</summary>
    public void HandleColdStart(IDictionary<string, string> arguments)
    {
        try { HandleArguments(arguments); }
        catch (Exception ex) { NotificationLog.Warn("cold-start activation failed", ex); }
    }

    private void HandleArguments(IDictionary<string, string> arguments)
    {
        if (arguments is null)
        {
            NotificationLog.Info("HandleArguments: null arguments");
            return;
        }

        NotificationLog.Info($"HandleArguments: [{string.Join(", ", arguments.Keys)}]");

        if (!arguments.TryGetValue("action", out var action))
        {
            NotificationLog.Info("HandleArguments: no 'action' key — ignoring");
            return;
        }
        if (action is not ("openConversation" or "openApproval"))
        {
            NotificationLog.Info($"HandleArguments: unhandled action '{action}'");
            return;
        }

        arguments.TryGetValue("agentId", out var agentId);
        arguments.TryGetValue("conversationId", out var conversationId);
        NotificationLog.Info($"HandleArguments: action={action} agentId={agentId} conversationId={conversationId}");
        Route(agentId, conversationId);
    }

    private void Route(string? agentId, string? conversationId)
    {
        lock (_gate)
        {
            if (!_ready)
            {
                // UI/navigation not ready yet — remember the latest click and replay later.
                NotificationLog.Info("Route: UI not ready — buffering for replay");
                _pending = (agentId, conversationId);
                return;
            }
        }

        NotificationLog.Info("Route: navigating now");
        _navigation.OpenConversation(agentId, conversationId);
    }

    /// <summary>Signals the UI is ready; replays any buffered cold-start click.</summary>
    public void MarkReady()
    {
        (string? AgentId, string? ConversationId)? pending;
        lock (_gate)
        {
            _ready = true;
            pending = _pending;
            _pending = null;
        }

        if (pending is { } p) _navigation.OpenConversation(p.AgentId, p.ConversationId);
    }
}
