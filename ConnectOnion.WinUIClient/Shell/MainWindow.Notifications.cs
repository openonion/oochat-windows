using System;
using System.Linq;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models.Notifications;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.Services.Notifications;
using ConnectOnion.WinUIClient.Views;
using Microsoft.UI.Xaml;

namespace ConnectOnion.WinUIClient;

/// <summary>
/// Notification integration for the shell window: registers the window with
/// <see cref="WindowPresenceService"/>, reports focus/visibility, and implements
/// <see cref="IChatWindow"/> so a clicked notification (or in-app toast) can
/// restore this window and open the target conversation.
/// </summary>
public sealed partial class MainWindow : IChatWindow
{
    private WindowPresenceService? _windowPresence;
    private int _notificationPresenceDetached;

    /// <summary>Called from the constructor: register for per-window presence tracking.</summary>
    private void InitializeNotificationPresence()
    {
        _windowPresence = AppServices.WindowPresence;
        _windowPresence.Register(this);
        Activated += OnActivatedForPresence;
        IdentityStore.IdentityReset += OnIdentityReset;
    }

    private void DetachNotificationPresence()
    {
        if (System.Threading.Interlocked.Exchange(ref _notificationPresenceDetached, 1) != 0) return;

        Activated -= OnActivatedForPresence;
        IdentityStore.IdentityReset -= OnIdentityReset;
        _windowPresence?.Unregister(this);
        _windowPresence = null;
    }

    /// <summary>
    /// Losing the stored identity is silent by nature — the app just carries on under a new
    /// address, and every authorization the old one had is gone. Say so. The identity is often
    /// first needed before this window exists, so both the latched flag (checked once the shell
    /// loads) and the live event route here.
    /// </summary>
    private void ReportIdentityResetIfAny()
    {
        if (!IdentityStore.WasReset || IdentityStore.ResetReason is not { } reason) return;
        OnIdentityReset(reason);
    }

    private void OnIdentityReset(string reason)
    {
        // The toast tells whoever is watching; this tells whoever reads the log afterwards.
        // IdentityStore itself can only reach Debug output, which Release builds compile away.
        Serilog.Log.Warning("Agent identity was reset and regenerated: {Reason}", reason);
        ShowIdentityResetToast(reason);
    }

    /// <summary>
    /// Shows the recovery phrase of an identity this run just minted, once.
    ///
    /// <para>An identity is created the first time anything needs to connect, which is normally
    /// before a window exists — so the phrase is latched in <see cref="IdentityStore"/> and
    /// presented here, at the first moment there is somewhere to present it. A phrase the user never
    /// sees is not a backup, which is the whole reason the identity is derived from one.</para>
    ///
    /// <para>Acknowledged as soon as it has been shown, not when the user confirms: dismissing the
    /// dialog loses nothing, because Settings → Identity can show it again at any time. Re-raising
    /// it on every launch until someone clicks the right button would just train people to dismiss
    /// it faster.</para>
    /// </summary>
    private async void RevealNewRecoveryPhraseIfAny()
    {
        if (IdentityStore.NewlyCreatedMnemonic is null) return;
        if (Content?.XamlRoot is not { } xamlRoot) return;

        IdentityStore.AcknowledgeNewMnemonic();

        try
        {
            var dialog = new Controls.RecoveryPhraseDialog(IdentityStore.ExportBackup(), isFirstReveal: true)
            {
                XamlRoot = xamlRoot,
            };
            await dialog.ShowThemedAsync();
        }
        catch (System.Exception ex)
        {
            // A dialog that cannot open (another one already up, window closing) must not take the
            // shell down with it — the phrase is still reachable from Settings.
            Serilog.Log.Warning(ex, "Could not show the new recovery phrase dialog");
        }
    }

    private void ShowIdentityResetToast(string reason)
        => DispatcherQueue.TryEnqueue(() => ShowInAppToast(new InAppToastModel(
            LocalizedStrings.Get("IdentityResetTitle", "New identity generated"),
            LocalizedStrings.Format(
                "IdentityResetBody",
                "Your previous ConnectOnion identity could not be recovered — {0}. Agents that authorized the old address will need to authorize this one again.",
                reason),
            NotificationType.Error,
            AgentId: null,
            ConversationId: null,
            ActionId: null)));

    private void OnActivatedForPresence(object sender, WindowActivatedEventArgs e)
    {
        var active = e.WindowActivationState != WindowActivationState.Deactivated;
        _windowPresence?.SetActive(this, active);
        // Coming back to a chat that was already on screen is not a navigation, so nothing
        // reloads the conversation and nothing would otherwise clear the badge it collected
        // while the window was in the background. Ordered after SetActive: the check below asks
        // presence what the user can see, and presence has to know the window is focused first.
        if (active) ClearAttentionForVisibleConversation();
    }

    /// <summary>
    /// Marks whatever conversation is genuinely on screen as read.
    ///
    /// <para>Attention is otherwise cleared only by <c>ChatViewModel</c>'s conversation load, which
    /// covers opening or switching a chat but not the two ways a chat becomes visible again without
    /// being reloaded: the window regaining focus, and a full-window modal closing over a page that
    /// never unloaded. Both marked the conversation unread on the way out (the user could not see
    /// it) and left the badge behind on the way back in.</para>
    ///
    /// <para>Safe to call on every activation: <c>ClearAttentionAsync</c>'s UPDATE is guarded on
    /// the row actually having unread state, so the common case touches no rows and raises no
    /// <c>SessionsChanged</c> — a focus change does not cost a sidebar rebuild.</para>
    /// </summary>
    private async void ClearAttentionForVisibleConversation()
    {
        if (_windowPresence?.VisibleConversationId is not { } conversationId) return;

        try
        {
            await AppServices.Sessions.ClearAttentionAsync(conversationId);
        }
        catch (System.Exception ex)
        {
            // Best-effort: a failed badge clear must never take down the window's activation path.
            NotificationLog.Warn("clearing conversation attention failed", ex);
        }
    }

    // ---- IChatWindow ----------------------------------------------------

    public void RestoreAndActivate() => RestoreFromTray();

    /// <summary>Selects the agent + conversation and navigates this window's frame to
    /// the chat. Fails safe (navigates Home) when the conversation was deleted.</summary>
    public async Task ShowConversationAsync(string agentId, string conversationId)
    {
        var session = await AppServices.Sessions.GetSessionAsync(conversationId);
        if (session is null || session.AgentId != agentId)
        {
            NotificationLog.Info($"ShowConversationAsync: no session matched agentId={agentId} conversationId={conversationId} — navigating Home");
            NavigateTo(typeof(HomePage), forceReload: true);
            return;
        }

        NotificationLog.Info($"ShowConversationAsync: matched session — opening chat for agentId={agentId} conversationId={conversationId}");

        await AppServices.Agents.SetSelectedAgentAsync(agentId);
        await AppServices.Sessions.SetActiveSessionAsync(conversationId);

        NavigateTo(typeof(ChatPage), forceReload: true);
    }

    public void ShowInAppToast(InAppToastModel toast) => InAppNotifications.ShowToast(toast);

    /// <summary>Finds the shell window hosting a given XamlRoot (so a page can report
    /// which conversation it is showing). Returns null if not found. Single-window app:
    /// the only candidate is <see cref="App.MainWindow"/>.</summary>
    internal static MainWindow? FromXamlRoot(XamlRoot? root)
    {
        if (root is null) return null;
        return App.MainWindow is MainWindow mainWindow
            && ReferenceEquals(mainWindow.Content?.XamlRoot, root)
            ? mainWindow
            : null;
    }
}
