using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>Pure builders for the documented interactive response values.</summary>
public static class InteractiveResponseBuilder
{
    /// <summary>
    /// What a masked value renders as. A fixed run of bullets, never one per character: a mask that
    /// tracks the length still publishes the length, which is a meaningful hint about a password.
    /// </summary>
    private const string Mask = "••••••";

    /// <summary>
    /// Whether a field's value must never be displayed or persisted.
    ///
    /// <para>The agent's own <c>type: "password"</c> is the primary signal, but it is not the only
    /// one: nothing forces an agent to set it, and a field called <c>api_key</c> typed as plain
    /// text is exactly as dangerous. The name check is the backstop.</para>
    /// </summary>
    public static bool IsSecretField(AskUserFieldEntry entry)
        => entry?.IsSecret == true;

    public static object? BuildAskUserAnswer(ChatMessage message)
    {
        if (message.AskUserFields.Count > 0)
        {
            var values = new Dictionary<string, string>();
            foreach (var entry in message.AskUserFields) values[entry.Name] = entry.Value.Trim();
            // WireJson, not JsonSerializer: this string goes on the socket, and the reflection
            // serializer throws under trimming (see docs/TRIMMING.md). Byte-identical output.
            return WireJson.SerializeStringMap(values);
        }

        if (!string.IsNullOrWhiteSpace(message.AskUserFreeText))
            return message.AskUserFreeText.Trim();

        var selected = message.AskUserOptionEntries
            .Where(option => option.IsChecked)
            .Select(option => option.Text)
            .ToList();
        if (selected.Count == 0) return null;
        return message.AskUserMultiSelect ? selected.ToArray() : selected[0];
    }

    /// <summary>
    /// The human-readable form of an answer, for the resolved card line and for the
    /// <c>event_meta</c> column behind it.
    ///
    /// <para><b>Separate from <see cref="BuildAskUserAnswer"/> on purpose, and this is the whole
    /// point of the method.</b> The wire answer must be complete — the agent asked for the password
    /// because it needs the password — but the summary is displayed in the transcript and
    /// <i>written to SQLite</i>, where it outlives the session that needed it. Stringifying the wire
    /// answer for display (which is what this replaced) put credentials in plaintext into the
    /// <c>messages</c> table, to be re-rendered on every reload, forever.</para>
    ///
    /// <para>Fields render as <c>label=value</c> pairs rather than raw JSON: the summary is prose
    /// shown to a person, and <c>{"username":"bob"}</c> was never that.</para>
    /// </summary>
    public static string BuildAskUserAnswerSummary(ChatMessage message)
    {
        if (message.AskUserFields.Count > 0)
        {
            return string.Join(" · ", message.AskUserFields.Select(entry =>
            {
                var label = string.IsNullOrWhiteSpace(entry.Label) ? entry.Name : entry.Label;
                var value = IsSecretField(entry) ? Mask : entry.Value.Trim();
                // An empty optional field says more as "(blank)" than as a dangling equals sign.
                return $"{label}={(value.Length == 0 ? "(blank)" : value)}";
            }));
        }

        if (!string.IsNullOrWhiteSpace(message.AskUserFreeText))
            return message.AskUserFreeText.Trim();

        return string.Join(", ", message.AskUserOptionEntries
            .Where(option => option.IsChecked)
            .Select(option => option.Text));
    }

    public static PlanReviewResponse? BuildPlanReviewResponse(
        PlanReviewAction action, string? feedback)
    {
        var trimmed = feedback?.Trim() ?? "";
        return action switch
        {
            PlanReviewAction.Approve => new PlanReviewResponse(
                trimmed,
                "Plan approved", false),
            PlanReviewAction.RequestChanges when trimmed.Length > 0 => new PlanReviewResponse(
                trimmed, "Changes requested", false),
            PlanReviewAction.Reject => new PlanReviewResponse(
                $"rejected: {(trimmed.Length == 0 ? "No reason provided" : trimmed)}",
                "Plan rejected", true),
            _ => null,
        };
    }
}

public sealed record PlanReviewResponse(string Message, string Outcome, bool Rejected);
