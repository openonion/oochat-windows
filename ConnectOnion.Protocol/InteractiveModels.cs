using System;
using System.Collections.Generic;
using System.Linq;

namespace ConnectOnion.Protocol;

/// <summary>A dynamic field in an ask_user form (name/label/placeholder/type).</summary>
public sealed record AskUserField(string Name, string Label, string? Placeholder, bool Required, string? Type);

/// <summary>
/// An <c>ask_user</c> turn: the agent is waiting for a human answer. Answer with
/// <see cref="AgentConnectionService.RespondAskUserAsync"/>.
/// </summary>
public sealed record AskUserRequest(
    string? Id,
    string Text,
    IReadOnlyList<string> Options,
    bool MultiSelect,
    IReadOnlyList<AskUserField> Fields);

/// <summary>
/// An <c>approval_needed</c> turn: the agent wants to run a tool. Respond with
/// <see cref="AgentConnectionService.RespondApprovalAsync"/>.
/// </summary>
public sealed record ApprovalRequest(
    string Tool,
    string? Description,
    string ArgumentsJson,
    string? Reason = null,
    string? BatchRemainingJson = null);

/// <summary>
/// A <c>plan_review</c> turn: the agent wants its plan reviewed. Respond with
/// <see cref="AgentConnectionService.RespondPlanReviewAsync"/>.
/// </summary>
public sealed record PlanReviewRequest(string PlanContent);

/// <summary>
/// An <c>ONBOARD_REQUIRED</c> trust gate: the agent will not talk to this address until it is
/// satisfied. Respond with <see cref="AgentConnectionService.SubmitOnboardInviteCodeAsync"/> or
/// <see cref="AgentConnectionService.SubmitOnboardPaymentAsync"/>.
/// </summary>
/// <param name="Methods">
/// What the agent will accept — <c>invite_code</c>, <c>payment</c>, or both. An empty list means the
/// host named none, which callers must treat as "offer the invite code": that is the near-universal
/// gate, and showing nothing would strand the user on an unanswerable card.
/// </param>
/// <param name="PaymentAmount">Amount the agent wants, when <c>payment</c> is on offer.</param>
/// <param name="PaymentAddress">
/// Where to send it, when the host says. Frequently absent — the reference clients only ever read
/// <c>payment_amount</c> off the wire — and it must never be guessed: an invented destination for a
/// real transfer loses the user's money. Callers show the address block only when it is present.
/// </param>
public sealed record OnboardRequest(
    IReadOnlyList<string> Methods,
    double? PaymentAmount = null,
    string? PaymentAddress = null)
{
    private static bool Has(IReadOnlyList<string> methods, string method)
        => methods.Any(m => string.Equals(m, method, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when an invite code is accepted — including when the host named no methods
    /// at all (see <see cref="Methods"/>).</summary>
    public bool AcceptsInviteCode => Methods.Count == 0 || Has(Methods, "invite_code");

    /// <summary>True when payment is accepted <i>and</i> an amount was quoted. A payment gate with
    /// no amount is not actionable, so it is not offered.</summary>
    public bool AcceptsPayment => Has(Methods, "payment") && PaymentAmount is > 0;
}
