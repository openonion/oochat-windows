using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Dispatching;

namespace ConnectOnion.WinUIClient.ViewModels;

/// <summary>
/// Shared online-status / offline-notice state for view models that track one
/// agent's reachability (the chat header and the agent detail page). Subclasses
/// point <see cref="PresenceAgent"/> at their current agent, call
/// <see cref="RaisePresenceProperties"/> whenever that agent changes, and add
/// their own presence-derived notifications by overriding
/// <see cref="OnPresenceRaised"/>. They must call <see cref="DetachPresence"/>
/// from their own cleanup so the shared presence event isn't leaked.
/// </summary>
public abstract partial class PresenceAwareViewModel : Common.ObservableObject
{
    protected DispatcherQueue Dispatcher { get; } = DispatcherQueue.GetForCurrentThread();
    protected AgentPresenceService PresenceService { get; }

    // True only while a user-initiated recheck is running, so the offline bar
    // stays visible (spinning) across the Offline→Checking→result transition
    // instead of vanishing the moment presence leaves Offline. Deliberately not
    // set by background probes, so an online agent never flashes the "offline"
    // bar while its first check runs.
    private bool _isManualRecheck;

    // True from construction until the page's own open-time probe resolves, so
    // opening a page never flashes the offline/reconnect bar off a stale cached
    // "offline" before a fresh probe has actually confirmed the current state.
    private bool _initialProbePending = true;

    protected PresenceAwareViewModel(AgentPresenceService presence)
    {
        PresenceService = presence;
        RecheckFailedMessage = "";
        PresenceService.PresenceChanged += OnPresenceChanged;
    }

    /// <summary>The agent whose presence this view model reflects, or null when none.</summary>
    protected abstract AgentConfig? PresenceAgent { get; }

    /// <summary>Detaches from the shared presence service. Call from the subclass's own cleanup.</summary>
    protected void DetachPresence()
        => PresenceService.PresenceChanged -= OnPresenceChanged;

    private void OnPresenceChanged(string agentId)
    {
        if (PresenceAgent is null || agentId != PresenceAgent.Id) return;
        Dispatcher.TryEnqueue(RaisePresenceProperties);
    }

    public bool HasPresenceAgent => PresenceAgent is not null;
    public AgentPresence Presence
        => PresenceAgent is null ? AgentPresence.Unknown : PresenceService.GetPresence(PresenceAgent.Id);
    public bool IsOnline => Presence == AgentPresence.Online;
    public bool IsOffline => Presence == AgentPresence.Offline;
    public bool ShowOfflineNotice => HasPresenceAgent && !_initialProbePending && (IsOffline || _isManualRecheck);
    /// <summary>True while a reachability probe is in flight (or a manual recheck is starting) — drives the offline bar's spinner.</summary>
    public bool IsCheckingPresence => _isManualRecheck || Presence == AgentPresence.Checking;
    public string PresenceDetail => PresenceAgent is null ? "" : PresenceService.GetDetail(PresenceAgent.Id);
    public string PresenceLabel => Presence switch
    {
        AgentPresence.Online => LocalizedStrings.Get("PresenceOnline", "Online"),
        AgentPresence.Checking => LocalizedStrings.Get("PresenceConnecting", "Connecting"),
        AgentPresence.Offline => LocalizedStrings.Get("PresenceOffline", "Offline"),
        _ => LocalizedStrings.Get("PresenceUnavailable", "Unavailable"),
    };

    /// <summary>Attention line shown in the offline bar after a manual recheck that resolved to still-offline.</summary>
    [ObservableProperty]
    public partial string RecheckFailedMessage { get; private set; }

    /// <summary>Re-probes the agent's online status (bound to the offline bar's
    /// Recheck button). Awaits the probe so the bar can spin while it runs and
    /// show a "still offline" note if it resolves without reconnecting.</summary>
    public async Task RecheckPresenceAsync()
    {
        var agent = PresenceAgent;
        if (agent is null) return;
        RecheckFailedMessage = "";
        _isManualRecheck = true;
        RaisePresenceProperties();
        try
        {
            await PresenceService.RefreshAsync(agent);
        }
        finally
        {
            _isManualRecheck = false;
            RecheckFailedMessage = IsOffline
                ? "Still offline. The agent hasn't reconnected — try again in a moment."
                : "";
            RaisePresenceProperties();
        }
    }

    /// <summary>Runs one fresh probe when the page opens, keeping the offline bar
    /// suppressed until it resolves. Call (fire-and-forget) from the subclass's
    /// load so the page never flashes the reconnect UI off a stale cached state.</summary>
    protected async Task ProbePresenceOnOpenAsync()
    {
        var agent = PresenceAgent;
        // Only a cached offline/unknown state can wrongly flash the reconnect bar
        // on open. A cached-online agent needs no forced re-probe here — and doing
        // one would briefly flip it to Checking and disable the composer.
        if (agent is null || Presence == AgentPresence.Online)
        {
            _initialProbePending = false;
            RaisePresenceProperties();
            return;
        }

        _initialProbePending = true;
        RaisePresenceProperties();
        try
        {
            await PresenceService.RefreshAsync(agent);
        }
        finally
        {
            _initialProbePending = false;
            RaisePresenceProperties();
        }
    }

    /// <summary>Raises change notifications for every presence-derived property.
    /// Subclasses add their own via <see cref="OnPresenceRaised"/>.</summary>
    protected void RaisePresenceProperties()
    {
        // A fresh probe (Checking) or a recovery (Online) clears any stale
        // "still offline" note before we re-notify the derived properties.
        if (Presence != AgentPresence.Offline) RecheckFailedMessage = "";

        OnPropertyChanged(nameof(HasPresenceAgent));
        OnPropertyChanged(nameof(Presence));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(ShowOfflineNotice));
        OnPropertyChanged(nameof(IsCheckingPresence));
        OnPropertyChanged(nameof(PresenceDetail));
        OnPropertyChanged(nameof(PresenceLabel));
        OnPresenceRaised();
    }

    /// <summary>Hook for subclasses to notify their own presence-derived properties
    /// (e.g. CanSend, CanStartConversation). Runs at the end of <see cref="RaisePresenceProperties"/>.</summary>
    protected virtual void OnPresenceRaised() { }
}
