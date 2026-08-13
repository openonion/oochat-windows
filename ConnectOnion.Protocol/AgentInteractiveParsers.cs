using System.Collections.Generic;
using System.Text.Json;

namespace ConnectOnion.Protocol;

/// <summary>
/// Public parsers for the interactive-turn wire frames (ask_user), shared by the
/// live <see cref="AgentConnectionService"/> and by any consumer that replays a
/// buffered raw frame (e.g. the desktop client's turn projection when reconstructing
/// an ask_user bubble for a page created mid-turn).
/// </summary>
public static class AgentInteractiveParsers
{
    /// <summary>Parses an <c>ask_user</c> frame into the request the UI renders. Total by
    /// design — every field degrades to an empty/default value rather than throwing, because a
    /// parse failure here would strand the agent on a question the user can never answer. A
    /// malformed frame is better shown as a bare prompt than not shown at all.</summary>
    public static AskUserRequest ParseAskUser(WireMessage msg)
    {
        // Non-string entries are dropped rather than stringified: an option is a literal the
        // client sends straight back, so anything of another shape has no valid answer form.
        var options = new List<string>();
        if (msg.TryGet("options", out var optsEl) && optsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var opt in optsEl.EnumerateArray())
            {
                if (opt.ValueKind == JsonValueKind.String) options.Add(opt.GetString()!);
            }
        }

        var fields = new List<AskUserField>();
        if (msg.TryGet("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fieldsEl.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object) continue;
                fields.Add(new AskUserField(
                    f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    f.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                    f.TryGetProperty("placeholder", out var p) ? p.GetString() : null,
                    // Required only when explicitly true — a missing or non-boolean value must
                    // not block submission on a field the agent never actually demanded.
                    f.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True,
                    f.TryGetProperty("type", out var t) ? t.GetString() : null));
            }
        }

        return new AskUserRequest(
            msg.GetString("id"),
            // "text" is current; "question" is what older agents send. Both spellings have to
            // keep working — the client cannot require an agent upgrade to ask a question.
            msg.GetString("text") ?? msg.GetString("question") ?? "",
            options,
            msg.GetBool("multi_select"),
            fields);
    }

    /// <summary>
    /// Parses an <c>ONBOARD_REQUIRED</c> frame. Total like <see cref="ParseAskUser"/>, and for a
    /// sharper reason: this frame stands between the user and the agent entirely, so a parse
    /// failure that produced nothing would leave them permanently unable to connect. Every field
    /// degrades — an unreadable <c>methods</c> array becomes empty, which
    /// <see cref="OnboardRequest.AcceptsInviteCode"/> reads as "offer the invite code".
    /// </summary>
    public static OnboardRequest ParseOnboard(WireMessage msg)
    {
        var methods = new List<string>();
        if (msg.TryGet("methods", out var methodsEl) && methodsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var method in methodsEl.EnumerateArray())
            {
                if (method.ValueKind == JsonValueKind.String && method.GetString() is { Length: > 0 } name)
                    methods.Add(name);
            }
        }

        double? amount = null;
        if (msg.TryGet("payment_amount", out var amountEl) && amountEl.ValueKind == JsonValueKind.Number
            && amountEl.TryGetDouble(out var parsed))
        {
            amount = parsed;
        }

        // Only ever read, never derived. The agent's own address is *not* a safe substitute: we do
        // not know that payment goes there, and an invented destination for a real transfer loses
        // the user's money. Absent means the card shows no address.
        var address = msg.TryGet("payment_address", out var addressEl)
            && addressEl.ValueKind == JsonValueKind.String
                ? addressEl.GetString()
                : null;

        return new OnboardRequest(methods, amount, string.IsNullOrWhiteSpace(address) ? null : address);
    }
}
