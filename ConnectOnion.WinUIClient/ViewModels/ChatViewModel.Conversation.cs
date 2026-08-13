using System;
using System.Linq;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.ViewModels;

/// <summary>
/// <see cref="ChatViewModel"/>: operations that change <i>which</i> conversation the page is on —
/// starting a new one, branching an existing one at an edited message, and retrying a turn in the
/// current conversation. Conversation-changing operations re-point the run subscription; retry
/// deliberately leaves it attached to the current session.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>
    /// Raised when this page swaps to a <i>different</i> conversation without navigating — which
    /// is what every operation in this file does. The page answers it by re-reporting which
    /// conversation it is showing to <c>WindowPresenceService</c>.
    ///
    /// <para><b>Why it has to exist.</b> Presence is reported from <c>ChatPage.OnLoaded</c>, and
    /// these operations re-point the page in place: no navigation, so no second OnLoaded, so the
    /// presence service went on naming the conversation the user had just branched <i>away</i>
    /// from. The notification policy then asked "is the user viewing this conversation?" about the
    /// new one, got false, and toasted the chat that was on screen — which is exactly what Retry
    /// and Edit did, because they branch and then immediately send.</para>
    ///
    /// <para>The view model raises it rather than reporting presence itself: resolving the host
    /// window goes through <c>XamlRoot</c>, which is the page's business, not this type's.</para>
    /// </summary>
    public event Action? ConversationChanged;

    public async Task NewConversationAsync()
    {
        if (_agent is null || IsProcessing) return;

        // Supersede any load still in flight, so a slow one can't land its history on top of the
        // conversation we are about to create.
        _loadGeneration++;

        // Re-point the subscription at the new conversation.
        _runSubscription?.Dispose();
        _runSubscription = null;
        _liveProjection = null;
        _liveRunId = null;
        _appliedEventCount = 0;
        _historyLoadedRunId = null;
        CanRetry = false;

        CanStop = false;
        IsStopping = false;
        _optimisticStopRunId = null;

        _session = SessionSummary.NewConversation(
            _agent.Id,
            await _sessions.CountForAgentAsync(_agent.Id),
            Common.SessionTitles.PlaceholderFormat);
        // A new conversation starts at the default mode rather than inheriting the last one: the
        // modes that skip approvals are exactly the ones nobody should acquire by accident.
        CurrentMode = _session.Mode;
        await _sessions.AppendSessionAsync(_session);

        Messages.Clear();
        ResetIdCounter();
        _createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        HeaderText = $"{_agent.DisplayName} - {_session.Title}";
        RaiseHeaderProperties();

        _runSubscription = SubscribeToRun(_session.Id);
        ConversationChanged?.Invoke();
    }

    /// <summary>
    /// Creates a real local branch at <paramref name="source"/>. Messages before the selected
    /// user turn are copied into a new conversation, then <paramref name="replacement"/> is sent
    /// as its first divergent turn. The original conversation remains untouched in the sidebar.
    /// </summary>
    public async Task<bool> BranchFromMessageAsync(ChatMessage source, string replacement)
    {
        replacement = replacement?.Trim() ?? "";
        if (_agent is null || _session is null || IsProcessing || !source.CanEdit || replacement.Length == 0)
        {
            return false;
        }

        var sourceIndex = Messages.IndexOf(source);
        if (sourceIndex < 0) return false;

        // Preserve the original page state before switching the active conversation.
        StoreSessionCache();

        // A branch copies everything before the selected turn into the new conversation. The whole
        // conversation is always loaded now, so the prefix is just the loaded bubbles above the
        // selected turn.
        var prefix = Messages.Take(sourceIndex).ToList();
        var inheritedMode = CurrentMode;

        _loadGeneration++;
        _runSubscription?.Dispose();
        _runSubscription = null;
        _liveProjection = null;
        _liveRunId = null;
        _appliedEventCount = 0;
        _historyLoadedRunId = null;
        CanRetry = false;
        CanStop = false;
        IsStopping = false;
        _optimisticStopRunId = null;

        _session = SessionSummary.NewConversation(
            _agent.Id,
            await _sessions.CountForAgentAsync(_agent.Id),
            Common.SessionTitles.PlaceholderFormat);
        _session.Mode = inheritedMode;
        CurrentMode = _session.Mode;
        await _sessions.AppendSessionAsync(_session);

        if (prefix.Count > 0)
        {
            await _conversations.UpsertMessagesAsync(_session.Id, prefix);
        }

        Messages.Clear();
        foreach (var message in prefix) Messages.Add(message);
        ResetIdCounter();
        _createdAt = prefix.Count > 0
            ? prefix[0].CreatedAtUnixMs
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        HeaderText = $"{_agent.DisplayName} - {_session.Title}";
        RaiseHeaderProperties();

        _runSubscription = SubscribeToRun(_session.Id);
        // Before the send, not after it: the turn this kicks off can raise an approval notification
        // while it runs, and the reply lands at the end of it. Reporting afterwards would leave the
        // whole turn notifying against the conversation we just left.
        ConversationChanged?.Invoke();

        await SendAsync(replacement);
        return true;
    }

    /// <summary>Re-sends an earlier user message in the current session. The previous turn remains
    /// visible and the new user bubble is appended at the end, so Retry never changes sidebar
    /// selection or creates a hidden branch.</summary>
    public async Task<bool> RetryUserMessageAsync(ChatMessage source)
    {
        if (_agent is null || _session is null || IsProcessing || !CanSend
            || !source.CanEdit || Messages.IndexOf(source) < 0)
        {
            return false;
        }

        await SendAsync(source.Content);
        return true;
    }

    /// <summary>Retries the user turn that produced an agent response in the current session by
    /// locating the nearest preceding user message. Event cards between the pair do not affect
    /// the lookup.</summary>
    public Task<bool> RetryFromAgentMessageAsync(ChatMessage response)
    {
        var responseIndex = Messages.IndexOf(response);
        if (responseIndex < 0) return Task.FromResult(false);

        for (var i = responseIndex - 1; i >= 0; i--)
        {
            if (Messages[i] is { IsUser: true } source)
            {
                return RetryUserMessageAsync(source);
            }
        }

        return Task.FromResult(false);
    }
}
