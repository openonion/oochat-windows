using System.Globalization;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>Adapts already-persisted legacy <c>EventKind == "tool"</c> bubbles into the
/// timeline model at load time. No database migration is needed; the next normal save writes
/// the new durable <c>tool_activity</c> payload.</summary>
public static class ToolActivityMigration
{
    /// <summary>Collapses each <i>run</i> of adjacent legacy tool bubbles into one
    /// <c>tool_activity</c> card, passing everything else through untouched. Adjacency is the
    /// grouping rule because it is all the old format preserved — there was no activity id — and
    /// it matches what the live projector does with consecutive calls today. Anything separating
    /// two tool bubbles (an assistant reply, a usage summary) therefore splits them into two
    /// cards, which is the right read: they belonged to different stretches of the turn.</summary>
    public static IReadOnlyList<ChatMessage> UpgradeLegacyToolMessages(IReadOnlyList<ChatMessage> source)
    {
        var upgraded = new List<ChatMessage>();
        // Manual index: the loop consumes a variable number of items per iteration, so nothing
        // advances i except the two branches below.
        for (var i = 0; i < source.Count;)
        {
            if (!source[i].IsToolEvent)
            {
                var message = source[i++];
                NormalizeLoadedMessage(message);
                upgraded.Add(message);
                continue;
            }

            var legacy = new List<ChatMessage>();
            while (i < source.Count && source[i].IsToolEvent) legacy.Add(source[i++]);
            // Compact and collapsed: this is history the user already scrolled past once.
            var activity = new ToolActivityViewModel { IsExpanded = false, DisplayMode = ToolDisplayMode.Compact };
            var projector = new ToolActivityProjector(activity);
            // Steps are built by hand rather than replayed through ApplyCall/ApplyResult —
            // there are no stream frames left, only the flattened fields the old format kept.
            // Re-running the projector's sanitizers is still worth it: legacy rows were written
            // before redaction existed.
            foreach (var old in legacy)
            {
                var step = new ToolStepViewModel
                {
                    // The old event key is the only stable id available; the row id is a
                    // per-conversation fallback for rows that never had one.
                    Id = old.EventKey ?? $"legacy-{old.Id}",
                    Sequence = activity.Steps.Count + 1,
                    ToolName = old.EventTitle,
                    DisplayName = ToolActivityProjector.DisplayName(old.EventTitle),
                    DisplayTarget = "",
                    Arguments = string.IsNullOrWhiteSpace(old.EventArgs) ? null : ToolActivityProjector.SanitizeJson(old.EventArgs),
                    Result = string.IsNullOrWhiteSpace(old.EventResult) ? null : ToolActivityProjector.SanitizeText(old.EventResult),
                    Summary = old.EventDetail,
                    DurationMs = ParseDuration(old.EventMeta),
                    Status = old.Status == EventStatus.Error ? ToolStepStatus.Failed : old.Status == EventStatus.Running ? ToolStepStatus.Running : ToolStepStatus.Success,
                    IsHighRisk = ToolActivityProjector.IsHighRisk(old.EventTitle),
                };
                activity.Steps.Add(step);
            }
            // Rolls the per-step statuses up into the card's summary — the same code path the
            // live turn uses, so a migrated card reads identically to a fresh one.
            projector.Complete();
            upgraded.Add(new ChatMessage
            {
                // Inherit the first legacy bubble's id so the card lands where the group did in
                // the ordering, and so the next save overwrites those rows instead of adding.
                Id = legacy[0].Id,
                Role = ChatRole.Event,
                EventKind = "tool_activity",
                EventTitle = "Tool execution",
                ToolActivity = activity,
            });
        }
        PairRelatedDiffApprovals(upgraded);
        return upgraded;
    }

    /// <summary>Restores the live diff/decision composition for cached and SQLite-backed history.
    /// A completed decision remains linked so its standalone row stays collapsed, while the diff
    /// retains the durable applied/rejected state and can still be expanded for inspection.</summary>
    private static void PairRelatedDiffApprovals(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var approval = messages[i];
            if (!approval.IsFileChangeApproval || approval.RelatedDiffPreview is not null) continue;

            var diff = messages.Take(i).LastOrDefault(candidate =>
                candidate.IsDiffPreviewEvent
                && candidate.RelatedDiffApproval is null
                && string.Equals(candidate.EventTitle, approval.AskUserTargetPath,
                    StringComparison.OrdinalIgnoreCase));
            if (diff is not null) approval.AttachRelatedDiffPreview(diff);
        }
    }

    /// <summary>
    /// A tool-activity restored from SQLite is history, never a live decision surface. Older
    /// builds could persist the card while it was parked on approval (or simply still running),
    /// leaving its durable status non-terminal even though the turn had already ended. Seal that
    /// stale state through the same roll-up used by a normally completed turn so history cannot
    /// advertise an approval the user can no longer answer.
    /// </summary>
    private static void NormalizeLoadedMessage(ChatMessage message)
    {
        // ConversationCache bypasses SQLite deserialization, so apply the same historical-card
        // presentation policy here as RowToMessage does for database-backed restores.
        if (message.IsInteractiveEvent) message.IsInteractiveCardExpanded = false;

        if (!message.IsToolActivityEvent || message.ToolActivity is not { } activity) return;

        // Approval is live-only. Clear defensively as well as relying on JsonIgnore, because an
        // in-memory ConversationCache entry can be restored without a serialization round trip.
        activity.Approval = null;
        if (!activity.IsTerminal) new ToolActivityProjector(activity).Complete();
        message.Status = activity.Status == ToolActivityStatus.Failed
            ? EventStatus.Error
            : EventStatus.Done;
        activity.RefreshPresentation();
    }

    /// <summary>Recovers a millisecond duration from the legacy meta string, which was written
    /// for display and never as data. The old writer was
    /// <c>ms &lt; 1000 ? $"{ms:0} ms" : $"{ms / 1000:0.0} s"</c>, so the only two shapes that
    /// can appear are "340 ms" and "1.5 s" — but a bare number is tolerated as milliseconds
    /// too, since a meta string that lost its unit is likelier to be raw ms than raw seconds.
    /// <para>The "ms" test has to come first: "ms" also contains an "s", so checking for the
    /// seconds suffix first would read every millisecond value as seconds and inflate it
    /// 1000×.</para></summary>
    private static double? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var number = new string(value.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return null;

        if (value.Contains("ms", StringComparison.OrdinalIgnoreCase)) return parsed;
        return value.Contains('s', StringComparison.OrdinalIgnoreCase) ? parsed * 1000 : parsed;
    }
}
