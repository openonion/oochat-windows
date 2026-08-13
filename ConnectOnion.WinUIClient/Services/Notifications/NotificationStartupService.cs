using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace ConnectOnion.WinUIClient.Services.Notifications;

/// <summary>
/// One-time startup wiring for the notification subsystem, run as an <see cref="IHostedService"/>
/// when the host starts (replaces the old <c>AppServices.InitializeNotifications</c>): register
/// for OS App-Notification activation and cancel a pending
/// "connection lost" notice when an agent comes back online.
/// </summary>
public sealed class NotificationStartupService : IHostedService
{
    private readonly AgentPresenceService _presence;
    private readonly NotificationActivationRouter _activation;
    private readonly NotificationCoordinator _coordinator;

    public NotificationStartupService(
        AgentPresenceService presence,
        NotificationActivationRouter activation,
        NotificationCoordinator coordinator)
    {
        _presence = presence;
        _activation = activation;
        _coordinator = coordinator;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _presence.PresenceChanged += OnPresenceChanged;
        _activation.RegisterAndListen();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _presence.PresenceChanged -= OnPresenceChanged;
        _activation.Unregister();
        return Task.CompletedTask;
    }

    private void OnPresenceChanged(string agentId)
    {
        // Recovery cancels a pending "connection lost" (grace period) or re-arms after one.
        if (_presence.GetPresence(agentId) == AgentPresence.Online)
            _coordinator.NotifyConnectionRestored(agentId);
    }
}
