using System;
using System.Linq;
using System.Threading;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Models.Notifications;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.Views;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace ConnectOnion.WinUIClient.Controls;

// User-interaction entry points for the sidebar: row hover/focus handlers and the
// routed Click handlers for add-chat / add-agent / icon / pin / delete / settings. Each
// delegates to the state/refresh logic in ShellSidebar.xaml.cs (RefreshAsync,
// SelectSessionAsync, ConfirmDeleteAsync, RequestNavigation);
// split out so the main file holds the data/presence model, not the wiring.
public sealed partial class ShellSidebar
{
    /// <summary>Guards the icon menu items against re-entry. Both are <c>async void</c> handlers
    /// that await a file picker, so without this a second click while the picker is open would
    /// commit a second file and leave the first orphaned on disk.</summary>
    private int _iconOperationInProgress;

    // Pre-compiled message delegates rather than the ILogger extension methods, which is what
    // CA1848 asks for and what NotificationLog already does.
    private static readonly Action<ILogger, string, Exception?> LogIconCleanupFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogIconCleanupFailed)),
            "Could not delete agent icon {IconPath}.");

    private static readonly Action<ILogger, string, Exception?> LogIconOperationFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, nameof(LogIconOperationFailed)),
            "Agent icon operation failed for {AgentId}.");

    private static readonly Action<ILogger, string, Exception?> LogRenameFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(3, nameof(LogRenameFailed)),
            "Renaming conversation {SessionId} failed.");

    private static readonly Action<ILogger, string, Exception?> LogDeleteFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(4, nameof(LogDeleteFailed)),
            "Deleting {TargetId} failed.");

    // Agent and session rows share one interaction model (see RowStateItem):
    // pointer hover and keyboard focus both reveal the row's action buttons and
    // paint its hover background. Both row kinds bind to these same handlers.
    //
    // The row is resolved through DataContext, and every row root in ShellSidebar.xaml therefore
    // carries an explicit DataContext="{x:Bind}". That is not redundant: ItemsRepeater only assigns
    // DataContext to a realized element when its DataTemplate has *no* compiled bindings — a
    // template carrying x:Bind gets ProcessBindings instead, and its DataContext stays null. So the
    // moment these templates moved from {Binding} to {x:Bind}, SetRowInteractive stopped matching,
    // which silently cost the whole sidebar its hover wash and its per-row action buttons.
    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e) => SetRowInteractive(sender, true);
    private void Row_PointerExited(object sender, PointerRoutedEventArgs e) => SetRowInteractive(sender, false);
    /// <summary>
    /// Arms the row for keyboard traversal only. GotFocus bubbles, so focus the framework assigns
    /// implicitly reaches this handler too — and an agent row carries no accent rail, which makes
    /// this wash its entire selected appearance. Clicking the sidebar's blank background therefore
    /// painted the first agent as selected when nothing had been selected at all. Pointer focus
    /// needs no arm of its own: Row_PointerEntered already covers every mouse case, and a row the
    /// pointer has left should not keep the wash merely because its button still holds focus.
    /// </summary>
    private void Row_GotFocus(object sender, RoutedEventArgs e)
    {
        if ((e.OriginalSource as Microsoft.UI.Xaml.Controls.Control)?.FocusState != FocusState.Keyboard) return;
        SetRowInteractive(sender, true);
    }

    private void Row_LostFocus(object sender, RoutedEventArgs e) => SetRowInteractive(sender, false);

    private static void SetRowInteractive(object sender, bool active)
    {
        if ((sender as FrameworkElement)?.DataContext is RowStateItem row)
        {
            row.SetInteractive(active);
        }
    }

    private void SearchSessions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement opener)
            SessionSearchRequested?.Invoke(opener);
    }

    /// <summary>
    /// Opens the agent's start-a-chat surface instead of creating a conversation outright.
    ///
    /// The session is created by <see cref="Views.AgentDetailPage"/> when the user actually sends
    /// their first message, so a click here that the user then thinks better of leaves nothing
    /// behind. Creating the row up front put an empty conversation in the sidebar for every
    /// stray click, and those had to be deleted by hand.
    /// </summary>
    private async void AddChat_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string agentId) return;

        await AppServices.Agents.SetSelectedAgentAsync(agentId);
        RevealAgentInSidebar(agentId);
        await RefreshAsync();
        RequestNavigation(typeof(Views.AgentDetailPage), forceReload: true);
    }

    private async void RenameAgent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string agentId) return;
        var host = MainWindow.FromXamlRoot(XamlRoot);
        if (host is not null) await host.RenameAgentAsync(agentId);
    }

    /// <summary>
    /// Replaces the agent's avatar with an image the user picks.
    ///
    /// The order — commit the file, then save the row, then delete the old file — is what keeps
    /// the two stores consistent under a failure: a committed file with no row is an orphan the
    /// next sweep can remove, whereas a row pointing at a file that was deleted first is a
    /// permanently broken avatar. Committed names carry a GUID, so the file just written can
    /// never be the one being deleted.
    /// </summary>
    private async void ChangeAgentIcon_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string agentId) return;
        if (Interlocked.Exchange(ref _iconOperationInProgress, 1) != 0) return;

        string? temporaryPath = null;
        try
        {
            temporaryPath = await AppServices.AgentIcons.PickTemporaryIconAsync();
            // Dismissing the picker is not a failure and gets no toast.
            if (temporaryPath is null) return;

            // Loaded after the picker closes, not before: the agent can be deleted from another
            // surface while a modal file dialog is open, and the stale copy would resurrect it.
            var agentsState = await AppServices.Agents.LoadAsync();
            var agent = agentsState.Agents.FirstOrDefault(entry => entry.Id == agentId);
            if (agent is null) return;

            var previousIconPath = agent.IconPath;
            var committedPath = await AppServices.AgentIcons.CommitTemporaryIconAsync(agentId, temporaryPath);
            // The commit moved the file, so the finally block below has nothing left to clean up.
            temporaryPath = null;

            try
            {
                if (!await AppServices.Agents.UpdateIconPathAsync(agentId, committedPath))
                {
                    await DeleteIconQuietlyAsync(committedPath);
                    return;
                }
            }
            catch
            {
                await DeleteIconQuietlyAsync(committedPath);
                throw;
            }

            await DeleteIconQuietlyAsync(previousIconPath);
            await RefreshAsync();
            RefreshCurrentAgentSurface();
        }
        catch (Exception exception)
        {
            ReportIconFailure(
                agentId,
                exception,
                "AgentIconChangeFailed",
                "The agent icon could not be changed.");
        }
        finally
        {
            await DeleteTemporaryIconQuietlyAsync(temporaryPath);
            Volatile.Write(ref _iconOperationInProgress, 0);
        }
    }

    /// <summary>Drops the custom icon and restores the name-initial avatar.</summary>
    private async void RemoveAgentIcon_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string agentId) return;
        if (Interlocked.Exchange(ref _iconOperationInProgress, 1) != 0) return;

        try
        {
            var agentsState = await AppServices.Agents.LoadAsync();
            var agent = agentsState.Agents.FirstOrDefault(entry => entry.Id == agentId);
            if (agent is null || string.IsNullOrWhiteSpace(agent.IconPath)) return;

            // Clear the reference before touching the file, for the same reason the change path
            // commits before saving: an orphaned file is recoverable, a dangling row is not.
            var previousIconPath = agent.IconPath;
            if (!await AppServices.Agents.UpdateIconPathAsync(agentId, null)) return;

            await DeleteIconQuietlyAsync(previousIconPath);
            await RefreshAsync();
            RefreshCurrentAgentSurface();
        }
        catch (Exception exception)
        {
            ReportIconFailure(
                agentId,
                exception,
                "AgentIconRemoveFailed",
                "The agent icon could not be removed.");
        }
        finally
        {
            Volatile.Write(ref _iconOperationInProgress, 0);
        }
    }

    /// <summary>Deletes a committed icon, swallowing failure. Every caller has already put the
    /// database in the state it wants; a file that outlives its row is untidy, not broken.</summary>
    private static async System.Threading.Tasks.Task DeleteIconQuietlyAsync(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;

        try
        {
            await AppServices.AgentIcons.DeleteIconAsync(relativePath);
        }
        catch (Exception exception)
        {
            LogIconCleanupFailed(
                AppServices.Logging.CreateLogger<ShellSidebar>(), relativePath, exception);
        }
    }

    /// <summary>
    /// Keeps the currently visible agent surface in sync with the sidebar after an icon change.
    ///
    /// Both pages already implement <see cref="IReloadablePage"/>, and <see cref="MainWindow"/>
    /// handles a forced same-page navigation by reloading that live instance in place. Reusing
    /// that path avoids a second icon-specific notification system and preserves the detail
    /// page's cached composer.
    /// </summary>
    private void RefreshCurrentAgentSurface()
    {
        if (_currentPageType == typeof(HomePage)
            || _currentPageType == typeof(AgentDetailPage))
        {
            RequestNavigation(_currentPageType, forceReload: true);
        }
    }

    private static async System.Threading.Tasks.Task DeleteTemporaryIconQuietlyAsync(string? temporaryPath)
    {
        if (string.IsNullOrWhiteSpace(temporaryPath)) return;

        try
        {
            await AppServices.AgentIcons.DeleteTemporaryIconAsync(temporaryPath);
        }
        catch (Exception exception)
        {
            LogIconCleanupFailed(
                AppServices.Logging.CreateLogger<ShellSidebar>(), temporaryPath, exception);
        }
    }

    /// <summary>
    /// Surfaces an icon failure as a toast rather than a dialog. The icon is decoration: a modal
    /// would interrupt whatever conversation is on screen to announce that an avatar stayed the
    /// way it already was.
    /// </summary>
    private void ReportIconFailure(string agentId, Exception exception, string resourceKey, string fallback)
    {
        LogIconOperationFailed(
            AppServices.Logging.CreateLogger<ShellSidebar>(), agentId, exception);

        MainWindow.FromXamlRoot(XamlRoot)?.ShowInAppToast(new InAppToastModel(
            LocalizedStrings.Get("AgentIconToastTitle", "Agent icon"),
            LocalizedStrings.Get(resourceKey, fallback),
            NotificationType.Error,
            AgentId: agentId,
            ConversationId: null,
            ActionId: null));
    }

    private async void DeleteAgent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string agentId) return;
        await DeleteAgentAsync(agentId);
    }

    internal async System.Threading.Tasks.Task<bool> DeleteAgentAsync(string agentId)
    {
        try
        {
            var agentsState = await AppServices.Agents.LoadAsync();
            var agent = agentsState.Agents.FirstOrDefault(entry => entry.Id == agentId);
            if (agent is null) return false;

            if (!await ConfirmDeleteAsync(
                    LocalizedStrings.Format("DeleteAgentTitle", "Delete {0}?", agent.DisplayName),
                    LocalizedStrings.Get(
                        "DeleteAgentWarning",
                        "This agent and all of its local sessions will be removed."))) return false;

            // Read before the row goes: once the agent is removed from storage nothing points at its
            // icon file any more, and the path would be unrecoverable.
            var iconPath = agent.IconPath;

            var activeSessionId = await AppServices.Sessions.GetActiveSessionIdAsync();
            var activeSession = activeSessionId is null
                ? null
                : await AppServices.Sessions.GetSessionAsync(activeSessionId);
            var deletingCurrentSurface =
                (_currentPageType == typeof(AgentDetailPage)
                    && agentsState.SelectedAgentId == agentId)
                || (_currentPageType == typeof(ChatPage) && activeSession?.AgentId == agentId);
            // Runtime sockets cannot participate in a SQLite transaction, so release them first.
            // The targeted repository delete below then removes every persisted child and the agent
            // row in one transaction, rolling all of it back if any statement fails.
            foreach (var removedId in await AppServices.Sessions.ListSessionIdsForAgentAsync(agentId))
            {
                ConversationCache.Invalidate(removedId);
                await AppServices.RunManager.ReleaseConversationAsync(removedId);
            }
            // Pick the desired fallback from the current UI state; the repository validates it
            // against the rows inside the transaction and falls back to the first remaining agent.
            var preferredAgentId = agentsState.SelectedAgentId == agentId
                ? (activeSession?.AgentId != agentId
                    ? activeSession?.AgentId
                    : agentsState.Agents.FirstOrDefault(entry => entry.Id != agentId)?.Id)
                : null;
            if (!await AppServices.Agents.DeleteAgentAsync(agentId, preferredAgentId)) return false;

            // The atomic delete clears the active pointer if it named one of the removed
            // conversations; choosing the replacement is this caller's decision.
            if (activeSession is not null && activeSession.AgentId == agentId)
            {
                var replacements = await AppServices.Sessions.LoadRecentAsync(1);
                await AppServices.Sessions.SetActiveSessionAsync(
                    replacements.Count > 0 ? replacements[0].Id : null);
            }

            // Only now: the row is gone, so deleting the file cannot leave a live agent pointing at
            // one that no longer exists.
            await DeleteIconQuietlyAsync(iconPath);

            AppServices.RunManager.ForgetAgent(agentId);
            AppServices.Presence.Forget(agentId);
            AppServices.Notifications.NotifyConnectionRestored(agentId);

            await RefreshAsync();
            if (deletingCurrentSurface)
            {
                // Agent Detail has no valid content once its agent is gone, and ChatPage is likewise
                // invalid after its active conversation graph is removed. Return to the stable
                // library root instead of manufacturing an empty chat page, and discard history that
                // could navigate back to the deleted entity.
                RequestNavigationReset(typeof(HomePage));
            }
            else if (_currentPageType == typeof(HomePage))
            {
                // The sidebar and HomePage own separate projections of the same agent rows. A
                // same-page navigation is otherwise ignored, so explicitly use the reload path to
                // remove the deleted row from the already-visible library.
                RequestNavigation(typeof(HomePage), forceReload: true);
            }
            return true;
        }
        catch (Exception exception)
        {
            ReportDeleteFailure(
                agentId,
                exception,
                "DeleteAgentFailed",
                "The agent could not be deleted. Nothing else was removed.");
            return false;
        }
    }

    /// <summary>A conversation row inside an agent's branch. Its owning branch remains visible
    /// without collapsing any other agents the user has expanded.</summary>
    private async void Session_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string sessionId)
        {
            await SelectSessionAsync(sessionId);
        }
    }

    /// <summary>A conversation row in the pinned section. Its owning agent is revealed without
    /// collapsing any other branches the user has open.</summary>
    private async void PinnedSession_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string sessionId)
        {
            await SelectSessionAsync(sessionId);
        }
    }

    private async void TogglePinSession_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string sessionId) return;

        var session = await AppServices.Sessions.GetSessionAsync(sessionId);
        if (session is null) return;

        session.IsPinned = !session.IsPinned;
        // Targeted write: pinned state lives in one app_meta row, so this never touches the
        // sessions table. SaveAsync would have upserted every conversation the user has.
        await AppServices.Sessions.SetPinnedAsync(session.Id, session.IsPinned);
        await RefreshAsync();
    }

    /// <summary>
    /// Renames a conversation from the sidebar's context menu. The title is otherwise derived
    /// once from the conversation's opening message and then frozen, so before this there was no
    /// way at all to tell two conversations apart beyond whatever their first message happened to
    /// say.
    ///
    /// <see cref="SessionSummary.TryRename"/> is what sets <c>HasCustomTitle</c>, which is what
    /// stops the *next* message from overwriting the name the user just chose.
    /// </summary>
    private async void RenameSession_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string sessionId) return;

        try
        {
            var session = await AppServices.Sessions.GetSessionAsync(sessionId);
            if (session is null) return;

            var input = new Microsoft.UI.Xaml.Controls.TextBox
            {
                Text = session.Title,
                SelectionStart = 0,
                SelectionLength = session.Title.Length,
                MaxLength = SessionSummary.MaxTitleLength,
                AcceptsReturn = false,
                TextWrapping = TextWrapping.NoWrap,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                input,
                LocalizedStrings.Get("RenameConversationFieldName", "Conversation name"));

            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = LocalizedStrings.Get("RenameConversationTitle", "Rename conversation"),
                Content = input,
                PrimaryButtonText = LocalizedStrings.Get("CommonSave", "Save"),
                CloseButtonText = LocalizedStrings.Get("CommonCancel", "Cancel"),
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            // Enter commits, matching every other single-field rename dialog on Windows. Without
            // this the TextBox swallows it and the only way to confirm is the mouse.
            input.KeyDown += (_, args) =>
            {
                if (args.Key != Windows.System.VirtualKey.Enter) return;
                args.Handled = true;
                dialog.Hide();
                _renameCommitted = true;
            };

            _renameCommitted = false;
            var result = await dialog.ShowThemedAsync();
            if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary && !_renameCommitted) return;

            if (!session.TryRename(input.Text)) return;

            // UpdateSessionAsync touches this one row rather than reconciling the whole index —
            // the same call the per-message title stamp uses.
            await AppServices.Sessions.UpdateSessionAsync(session);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            LogRenameFailed(AppServices.Logging.CreateLogger<ShellSidebar>(), sessionId, ex);
        }
    }

    /// <summary>Set when Enter committed the rename dialog, because <see cref="ContentDialog.Hide"/>
    /// reports <see cref="ContentDialogResult.None"/> rather than the primary result.</summary>
    private bool _renameCommitted;

    private async void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string sessionId) return;

        try
        {
            var session = await AppServices.Sessions.GetSessionAsync(sessionId);
            if (session is null) return;

            if (!await ConfirmDeleteAsync(
                    LocalizedStrings.Format("DeleteConversationTitle", "Delete {0}?", session.Title),
                    LocalizedStrings.Get(
                        "DeleteConversationWarning",
                        "This local conversation will be removed."))) return;

            var wasActive = await AppServices.Sessions.GetActiveSessionIdAsync() == sessionId;

            ConversationCache.Invalidate(sessionId);
            await AppServices.RunManager.ReleaseConversationAsync(sessionId);
            // Children, index row, pin, and active pointer commit as one unit.
            await AppServices.Sessions.DeleteSessionAsync(sessionId);

            // No conversations left for this agent — go back to the agent page
            // instead of silently starting a fresh conversation, so deleting the
            // last chat doesn't just spawn another one in its place.
            var remainingForAgent = await AppServices.Sessions.LoadAgentSessionsAsync(session.AgentId, limit: 1);
            if (remainingForAgent.Sessions.Count == 0)
            {
                await AppServices.Agents.SetSelectedAgentAsync(session.AgentId);

                await RefreshAsync();
                RequestNavigation(typeof(AgentDetailPage), forceReload: true);
                return;
            }

            // DeleteSessionAsync already dropped the pointer if it named this conversation; pick the
            // successor here — the agent's most recent, else the most recent anywhere.
            // Count > 0 is established above, so the agent's own most recent always exists here.
            if (wasActive) await AppServices.Sessions.SetActiveSessionAsync(remainingForAgent.Sessions[0].Id);

            await RefreshAsync();
            RequestNavigation(typeof(ChatPage), forceReload: true);
        }
        catch (Exception exception)
        {
            ReportDeleteFailure(
                sessionId,
                exception,
                "DeleteConversationFailed",
                "The conversation could not be deleted. Please try again.");
        }
    }

    private void ReportDeleteFailure(
        string targetId,
        Exception exception,
        string resourceKey,
        string fallback)
    {
        LogDeleteFailed(AppServices.Logging.CreateLogger<ShellSidebar>(), targetId, exception);
        MainWindow.FromXamlRoot(XamlRoot)?.ShowInAppToast(new InAppToastModel(
            LocalizedStrings.Get("DeleteFailedTitle", "Delete failed"),
            LocalizedStrings.Get(resourceKey, fallback),
            NotificationType.Error,
            AgentId: null,
            ConversationId: null,
            ActionId: null));
    }

    private async void AddAgent_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        if (sender is FrameworkElement opener)
            AddAgentRequested?.Invoke(opener);
    }

    private async void BottomSettings_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        if (sender is FrameworkElement opener)
            SettingsRequested?.Invoke(opener);
    }
}
