using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>
/// Which interactive card, if any, the agent is currently blocked on.
///
/// <para>A pure scan over the transcript, in <c>Core</c> rather than beside its one caller in
/// <c>ChatViewModel</c>, so it can be covered headlessly — the view model itself lives in the app
/// project and no test host can load that. The caller keeps the wording and the UI; this keeps the
/// rule.</para>
/// </summary>
public static class PendingDecision
{
    /// <summary>The card blocking the turn, or null when nothing is waiting on the user.
    ///
    /// <para><b>Backwards, and it stops at the first hit.</b> Interactive turns are sequential —
    /// the host parks on each one before producing the next — so at most one card is genuinely
    /// blocking, and it is the most recent. Scanning forwards would find a card left
    /// <see cref="EventStatus.Running"/> by an earlier turn that ended abnormally and report a
    /// decision the user can no longer make.</para>
    ///
    /// <para>Includes rows with no transcript row of their own. An <c>approval_needed</c> is
    /// rendered by its owning tool-activity card and an unanswered file-change <c>ask_user</c> by
    /// its diff, but both are in the list and both block the turn — asking "is it visible" here
    /// would miss exactly the cards whose position is hardest to find, which is the case this
    /// exists for.</para></summary>
    public static ChatMessage? Find(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.IsInteractiveEvent && message.Status == EventStatus.Running) return message;
        }

        return null;
    }
}
