namespace ConnectOnion.WinUIClient.Models;

/// <summary>
/// Adds each turn's <c>turn_usage</c> summary on the agent reply that ends the turn, so the
/// application can show it on demand (info button in the reply's hover actions)
/// </summary>
public static class ChatTurnUsageLinker
{
    /// <summary>Links every agent reply in the list.</summary>
    public static void LinkAll(IList<ChatMessage> messages)
    {
        for (var i = 0; i < messages.Count; i++)
            LinkPrecedingUsage(messages, i);
    }

    /// <summary>If <paramref name="messages"/>[<paramref name="index"/>] is an agent reply, finds
    /// the nearest preceding usage row within the same turn (stops at the user message
    /// that opened it) and copies usage summary onto the reply.</summary>
    public static void LinkPrecedingUsage(IList<ChatMessage> messages, int index)
    {
        if (index < 0 || index >= messages.Count) return;
        var reply = messages[index];

        if (reply.Role != ChatRole.Agent) return;

        for (var i = index - 1; i >= 0; i--)
        {
            var msg = messages[i];

            if (msg.Role == ChatRole.User) return;
            if (msg.Role == ChatRole.Agent) return;
            if (msg.IsTurnUsageEvent)
            {
                reply.TurnUsageInfo = msg.TurnUsageDisplayText;
                return;
            }
        }
    }
}
