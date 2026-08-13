using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient;

public sealed partial class MainWindow
{
    private const int MaxTrayRecentChats = 3;

    /// <summary>Opening the menu is the final freshness check because title-only repository
    /// updates deliberately do not raise SessionsChanged.</summary>
    private async void TrayContextMenu_Opening(object sender, object e)
    {
        try { await RefreshTrayRecentChatsAsync(); }
        catch { /* Keep the existing cached shortcuts if storage is temporarily unavailable. */ }
    }

    private async Task OpenRecentChatFromTrayAsync(SessionSummary session)
    {
        BringToForeground();
        await ShowConversationAsync(session.AgentId, session.Id);
    }

    private async Task RefreshTrayRecentChatsAsync()
    {
        if (_isExiting || _isTrayDisposed) return;

        var sessionsTask = AppServices.Sessions.LoadAsync();
        // Names only, and never written back — the thin read skips every agent's /info blob.
        var agentsTask = AppServices.Agents.LoadSummariesAsync();
        await Task.WhenAll(sessionsTask, agentsTask);

        if (_isExiting || _isTrayDisposed) return;

        var agentNames = agentsTask.Result.Agents.ToDictionary(
            agent => agent.Id,
            agent => agent.Name,
            StringComparer.Ordinal);
        var orderedSessions = sessionsTask.Result.Sessions
            // Defense in depth for databases written by an older build: the v4 migration removes
            // orphan sessions, but a global shell surface should never expose one even if storage
            // repair is interrupted or the database is copied from an old profile.
            .Where(session => agentNames.ContainsKey(session.AgentId))
            .OrderByDescending(session => ParseTrayTimestamp(session.UpdatedAt))
            .ToArray();
        var recent = orderedSessions
            .Take(MaxTrayRecentChats)
            .ToArray();
        var remaining = orderedSessions
            .Skip(MaxTrayRecentChats)
            .ToArray();

        var separatorIndex = TrayContextMenu.Items.IndexOf(RecentChatsEndSeparator);
        while (separatorIndex > 1)
        {
            TrayContextMenu.Items.RemoveAt(1);
            separatorIndex--;
        }

        if (recent.Length == 0)
        {
            TrayContextMenu.Items.Insert(1, new MenuFlyoutItem
            {
                Text = LocalizedStrings.Get("TrayNoRecentChats", "No recent chats"),
                IsEnabled = false,
            });
            return;
        }

        for (var index = 0; index < recent.Length; index++)
        {
            TrayContextMenu.Items.Insert(
                index + 1,
                CreateTraySessionItem(recent[index], agentNames));
        }

        if (remaining.Length > 0)
        {
            var moreItem = new MenuFlyoutSubItem
            {
                Text = LocalizedStrings.Get("CommonMore", "More"),
            };
            // PopupMenu mode maps this submenu to a native HMENU. Windows constrains an
            // oversized submenu to the monitor work area and supplies native wheel/arrow
            // scrolling, so the full remainder stays browsable without a custom flyout.
            foreach (var session in remaining)
            {
                moreItem.Items.Add(CreateTraySessionItem(session, agentNames));
            }

            TrayContextMenu.Items.Insert(recent.Length + 1, moreItem);
        }
    }

    private MenuFlyoutItem CreateTraySessionItem(
        SessionSummary session,
        System.Collections.Generic.IReadOnlyDictionary<string, string> agentNames)
    {
        var agentName = agentNames.TryGetValue(session.AgentId, out var name)
            ? name
            : LocalizedStrings.Get("UnknownAgent", "Unknown agent");
        var item = new MenuFlyoutItem
        {
            Text = CompactTrayLabel(session.Title, agentName),
            Command = new AsyncRelayCommand(
                () => OpenRecentChatFromTrayAsync(session)),
        };
        ToolTipService.SetToolTip(item, $"{session.Title} - {agentName}");
        return item;
    }

    private static DateTimeOffset ParseTrayTimestamp(string value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private static string CompactTrayLabel(string title, string agentName)
    {
        const int maxTitleLength = 36;
        var compactTitle = title.Length <= maxTitleLength
            ? title
            : $"{title[..(maxTitleLength - 3)]}...";
        return $"{compactTitle}  -  {agentName}";
    }
}
