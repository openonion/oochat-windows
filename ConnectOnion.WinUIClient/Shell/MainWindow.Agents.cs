using System;
using System.Linq;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Models.Notifications;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.Views;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ConnectOnion.WinUIClient;

public sealed partial class MainWindow
{
    private bool _renameAgentCommitted;

    /// <summary>Shows the one rename workflow shared by the sidebar and Settings, then refreshes
    /// every currently visible projection of the agent's local display name.</summary>
    internal async Task<bool> RenameAgentAsync(string agentId)
    {
        var state = await AppServices.Agents.LoadAsync();
        var agent = state.Agents.FirstOrDefault(entry => entry.Id == agentId);
        if (agent is null) return false;

        var input = new TextBox
        {
            Text = agent.DisplayName,
            SelectionStart = 0,
            SelectionLength = agent.DisplayName.Length,
            MaxLength = AgentConfig.MaxNameLength,
            AcceptsReturn = false,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.NoWrap,
        };
        AutomationProperties.SetName(
            input,
            LocalizedStrings.Get("RenameAgentFieldName", "Agent name"));
        AutomationProperties.SetAutomationId(input, "RenameAgentInput");

        var dialog = new ContentDialog
        {
            Title = LocalizedStrings.Get("RenameAgentTitle", "Rename agent"),
            Content = input,
            PrimaryButtonText = LocalizedStrings.Get("CommonSave", "Save"),
            CloseButtonText = LocalizedStrings.Get("CommonCancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = AgentConfig.IsValidName(input.Text),
            XamlRoot = RootGrid.XamlRoot,
        };

        input.TextChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = AgentConfig.IsValidName(input.Text);
        input.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter || !dialog.IsPrimaryButtonEnabled) return;
            args.Handled = true;
            _renameAgentCommitted = true;
            dialog.Hide();
        };

        _renameAgentCommitted = false;
        var result = await dialog.ShowThemedAsync();
        if (result != ContentDialogResult.Primary && !_renameAgentCommitted) return false;
        if (!agent.TryRename(input.Text)) return false;

        try
        {
            if (!await AppServices.Agents.UpdateNameAsync(agent.Id, agent.Name)) return false;
        }
        catch
        {
            ShowInAppToast(new InAppToastModel(
                LocalizedStrings.Get("AgentRenameToastTitle", "Agent name"),
                LocalizedStrings.Get("RenameAgentFailed", "The agent could not be renamed."),
                NotificationType.Error,
                AgentId: agent.Id,
                ConversationId: null,
                ActionId: null));
            return false;
        }

        try { await ShellSidebar.RefreshAsync(); }
        catch { /* The persisted name will appear on the next sidebar refresh. */ }
        try { if (_settingsOverlay is not null) await _settingsOverlay.RefreshAgentsAsync(); }
        catch { /* The settings list retries when the category is reopened. */ }
        try { await RefreshRenamedAgentSurfaceAsync(agent.Id); }
        catch { /* The current page reloads from storage on its next navigation. */ }
        try { await RefreshTrayRecentChatsAsync(); }
        catch { /* The tray menu performs its own freshness check when opened. */ }
        return true;
    }

    private async Task RefreshRenamedAgentSurfaceAsync(string agentId)
    {
        switch (ContentFrame.Content)
        {
            case HomePage homePage:
                await homePage.ReloadAsync();
                break;
            case AgentDetailPage agentDetailPage:
                await agentDetailPage.ReloadAsync();
                break;
            case ChatPage chatPage:
                await chatPage.Vm.RefreshAgentPresentationAsync(agentId);
                break;
        }
    }
}
