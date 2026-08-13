using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Common;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using ConnectOnion.WinUIClient.Services.Attachments;

namespace ConnectOnion.WinUIClient.ViewModels;

/// <summary>
/// Interactive-turn responses and inbound-image resolution for <see cref="ChatViewModel"/>.
/// The stream → bubble mapping itself now lives in <see cref="Services.Runtime.ChatTurnProjection"/>
/// (shared with app-level persistence); this file only handles the parts that need a live
/// socket for <b>this</b> page: sending the user's answer/approval and caching agent images.
///
/// Interactive bubbles are built by the projection from buffered events; answering one sends
/// the response on the app-owned connection (fetched from the run manager's connection
/// registry). The live control state is intentionally not persisted — once the socket's
/// io.receive() wait is gone, answering is meaningless — matching the reference client.
/// </summary>
public sealed partial class ChatViewModel
{
    private AgentConnectionService? CurrentConnection
        => _session is null ? null : _runManager.Connections.Get(_session.Id);

    private Guid? BeginInteractiveAnswer(string meta, EventStatus status)
        => _session is null
            ? null
            : _runManager.BeginInteractiveAnswer(_session.Id, meta, status);

    private void ConfirmInteractiveAnswer(Guid? reservation)
    {
        if (reservation is { } id) _runManager.ConfirmInteractiveAnswer(id);
        if (_session is not null) _runManager.ClearPendingInteractive(_session.Id);
    }

    private void CancelInteractiveAnswer(Guid? reservation)
    {
        if (reservation is { } id) _runManager.CancelInteractiveAnswer(id);
    }

    public async Task SubmitOnboardInviteCodeAsync(ChatMessage message)
    {
        var connection = CurrentConnection;
        if (connection is null) return;

        var inviteCode = message.OnboardInviteCode.Trim();
        if (inviteCode.Length == 0) return;

        message.IsOnboardingSubmitted = true;
        message.Content = LocalizedStrings.Get(
            "ChatInviteSubmitted",
            "Invite code submitted. Waiting for the agent to finish onboarding...");
        OnPropertyChanged(nameof(Messages));

        try
        {
            await connection.SubmitOnboardInviteCodeAsync(inviteCode);
        }
        catch (Exception ex)
        {
            connection.InviteCode = null;
            message.IsOnboardingSubmitted = false;
            message.Content = LocalizedStrings.Format(
                "ChatInviteFailed",
                "Invite code submission failed: {0}",
                ex.Message);
        }
    }

    /// <summary>
    /// Answers the payment branch of an onboarding gate. The button is the user asserting that a
    /// transfer already happened — this sends that claim and nothing else; no money moves here, and
    /// the agent verifies it independently before it lets the conversation through.
    /// </summary>
    public async Task SubmitOnboardPaymentAsync(ChatMessage message)
    {
        var connection = CurrentConnection;
        if (connection is null) return;
        if (message.OnboardRequest?.PaymentAmount is not { } amount || amount <= 0) return;

        message.IsOnboardingSubmitted = true;
        message.Content = LocalizedStrings.Get(
            "ChatPaymentReported",
            "Payment reported. Waiting for the agent to verify it...");
        OnPropertyChanged(nameof(Messages));

        try
        {
            await connection.SubmitOnboardPaymentAsync(amount);
        }
        catch (Exception ex)
        {
            // Re-arms the whole card, not just the payment row: whichever method the user tries
            // next, the gate is still standing.
            message.IsOnboardingSubmitted = false;
            message.Content = LocalizedStrings.Format(
                "ChatPaymentFailed",
                "Could not report the payment: {0}",
                ex.Message);
        }
    }

    /// <summary>Re-arms an invite-code card whose submission never reached ONBOARD_SUCCESS
    /// (rejected code, dropped socket) so the user can try again. The stored code is dropped
    /// with it — the connection auto-submits <see cref="AgentConnectionService.InviteCode"/>
    /// on the next ONBOARD_REQUIRED, and replaying a code the agent just refused would only
    /// fail again without the user ever seeing the box.</summary>
    private void ReopenPendingOnboarding()
    {
        var reopened = false;
        foreach (var message in Messages)
        {
            if (!message.IsOnboarding || !message.IsOnboardingSubmitted) continue;
            message.IsOnboardingSubmitted = false;
            message.Content = LocalizedStrings.Get(
                "ChatOnboardingIncomplete",
                "Onboarding did not complete. Check the invite code and try again.");
            reopened = true;
        }
        if (reopened && CurrentConnection is { } connection) connection.InviteCode = null;
    }

    /// <summary>Submits an ask_user bubble's answer: dynamic fields → JSON, multi-select →
    /// comma-joined checked options, else free text or the selected radio option.</summary>
    public async Task SubmitAskUserAnswerAsync(ChatMessage message)
    {
        if (!message.ValidateAskUser()) return;
        var answer = Services.Runtime.InteractiveResponseBuilder.BuildAskUserAnswer(message);
        if (answer is null || !message.TryBeginInteractiveSubmit()) return;

        var connection = CurrentConnection;
        if (connection is not { IsConnected: true })
        {
            message.FailInteractiveSubmit("Reconnect before sending this answer.", connectionLost: true);
            return;
        }

        // Remember the credentials before they leave: the agent's very next move is usually to type
        // this password into a browser, and that tool call's arguments are persisted too. Masking
        // it here only closes the first of the two doors into the database.
        foreach (var field in message.AskUserFields)
        {
            if (Services.Runtime.InteractiveResponseBuilder.IsSecretField(field))
                Services.Runtime.SessionSecrets.Remember(field.Value);
        }

        // Never stringify `answer` for this: it is the wire payload, and for a credentials form it
        // holds the password in plaintext. The summary is what reaches the card and the persisted
        // event_meta column, so it goes through the builder that masks secret fields.
        var outcome = $"Answered: {Services.Runtime.InteractiveResponseBuilder.BuildAskUserAnswerSummary(message)}";
        var reservation = BeginInteractiveAnswer(outcome, EventStatus.Done);
        try
        {
            await connection.RespondAskUserAsync(answer);
            ConfirmInteractiveAnswer(reservation);
            message.CompleteInteractiveSubmit(outcome);
            RefreshPendingInteractive();
        }
        catch (Exception ex)
        {
            CancelInteractiveAnswer(reservation);
            message.FailInteractiveSubmit(ex.Message, connectionLost: !connection.IsConnected);
        }
    }

    /// <summary>
    /// The text a skip puts on the wire. There is no skip primitive in the protocol —
    /// <c>ASK_USER_RESPONSE</c> carries a single free-text <c>answer</c> — so this string is read
    /// by the agent's model, and every clause in it is load-bearing:
    ///
    /// <list type="bullet">
    /// <item>Third person, stating what happened rather than commanding, so it reads as the state
    /// of the turn instead of as the user issuing an instruction.</item>
    /// <item>"will not answer" closes the door; without it the model can treat the skip as a
    /// deferral and wait for something that is never coming.</item>
    /// <item>"Proceed without this information" rather than anything about judgement — the latter
    /// invites the model to invent the answer it was asking for.</item>
    /// <item>"do not ask this question again", scoped to <i>this</i> question. Unscoped, it reads
    /// as a ban on asking anything, which would suppress legitimate later questions. The clause
    /// exists because re-asking a skipped question in different words is a real failure mode.</item>
    /// </list>
    ///
    /// It is a prompt, not a guarantee: whether the agent honours it is up to its model.
    /// </summary>
    private const string SkipAnswer =
        "User skipped this question and will not answer. "
        + "Proceed without this information; do not ask this question again.";

    /// <summary>
    /// Declines an ask_user turn. This still <i>sends</i> — the agent is parked on
    /// <c>io.receive()</c> and only moves when something arrives, so a skip that merely dismissed
    /// the card would hang the turn forever.
    /// </summary>
    public async Task SkipAskUserAsync(ChatMessage message)
    {
        if (!message.TryBeginInteractiveSubmit()) return;
        var connection = CurrentConnection;
        if (connection is not { IsConnected: true })
        {
            message.FailInteractiveSubmit("Reconnect before skipping this question.", connectionLost: true);
            return;
        }

        var reservation = BeginInteractiveAnswer("Skipped", EventStatus.Done);
        try
        {
            await connection.RespondAskUserAsync(SkipAnswer);
            ConfirmInteractiveAnswer(reservation);
            message.CompleteInteractiveSubmit("Skipped");
            RefreshPendingInteractive();
        }
        catch (Exception ex)
        {
            CancelInteractiveAnswer(reservation);
            message.FailInteractiveSubmit(ex.Message, connectionLost: !connection.IsConnected);
        }
    }

    /// <summary>
    /// Answers an <b>embedded</b> approval (the card inside the tool-activity card). The command on
    /// the message has already flipped it to <see cref="ApprovalCardPhase.Submitting"/> synchronously,
    /// so this cannot be entered twice for one decision; here we only do the socket round-trip and
    /// settle the phase.
    ///
    /// <para>Maps the five <see cref="ApprovalAction"/>s onto the approval wire call: Allow once /
    /// Trust are affirmatives (scope
    /// once/session); Reject is the <see cref="ApprovalRejectModes.Soft"/> decline that lets the
    /// agent keep working; Stop is the <see cref="ApprovalRejectModes.Hard"/> halt that ends the
    /// turn. The two are genuinely different scopes, which is why the card shows them apart.</para>
    ///
    /// <para>The answer is recorded (for the persist path's FIFO) and the phase settled only
    /// <i>after</i> a successful send, so a failed attempt the user retries never enqueues the
    /// answer twice. On failure the original decision surface becomes actionable again and the
    /// transport error is reported through the normal activity channel; the card has no separate
    /// fallback/retry face.</para>
    /// </summary>
    public async Task HandleApprovalDecisionAsync(ChatMessage message, ApprovalAction action)
    {
        var (approved, scope, rejectMode, meta) = action switch
        {
            ApprovalAction.AllowOnce => (true, "once", ApprovalRejectModes.Hard, "Approved once"),
            ApprovalAction.TrustSession => (true, "session", ApprovalRejectModes.Hard, "Approved for session"),
            ApprovalAction.Reject => (false, "once", ApprovalRejectModes.Soft, "Skipped"),
            ApprovalAction.Stop => (false, "once", ApprovalRejectModes.Hard, "Stopped"),
            ApprovalAction.Explain => (false, "once", ApprovalRejectModes.Explain, "Explanation requested"),
            _ => (true, "once", ApprovalRejectModes.Hard, "Approved once"),
        };

        var connection = CurrentConnection;
        if (connection is not { IsConnected: true })
        {
            message.FailApproval("Reconnect before responding to this approval.");
            return;
        }
        var reservation = BeginInteractiveAnswer(meta, approved ? EventStatus.Done : EventStatus.Error);
        try
        {
            await connection.RespondApprovalAsync(
                approved, scope, rejectMode, message.ApprovalFeedback.Trim());
            ConfirmInteractiveAnswer(reservation);
        }
        catch (Exception ex)
        {
            CancelInteractiveAnswer(reservation);
            message.FailApproval(ex.Message);
            return;
        }

        // The agent is parked on this approval, so the turn cannot complete (and persist) until the
        // response above lands — recording here, after a successful send, is both in time for the
        // persist FIFO and safe against a retried failure double-enqueuing the answer.
        message.EventMeta = meta;
        message.Status = action == ApprovalAction.Stop ? EventStatus.Error : EventStatus.Done;
        message.CompleteApproval(action);
        // The approval's Status is set above rather than by CompleteInteractiveSubmit, so this
        // path refreshes here instead of beside a shared call.
        RefreshPendingInteractive();

        if (action == ApprovalAction.Stop)
        {
            _runManager.ClearPendingInteractive(_session!.Id);
            if (message.OwnerToolActivity is { } stoppedActivity)
            {
                // Keep the resolved approval attached as a compact audit result. Removing it here
                // made "Task stopped" disappear at the exact moment the user chose it.
                stoppedActivity.Status = Models.ToolActivityStatus.Cancelled;
            }
            Messages.Add(new ChatMessage
            {
                Id = NextId(),
                Role = ChatRole.Event,
                EventKind = "activity",
                EventTitle = LocalizedStrings.Get("ChatUserStoppedTask", "User stopped the task"),
                Status = EventStatus.Done,
            });

            var run = _runManager.GetActiveRun(_session.Id);
            if (run is { IsTerminal: false })
                await _runManager.CancelRunAsync(_session.Id, run.RunId);
            IsProcessing = false;
            CanStop = false;
            IsStopping = false;
            _optimisticStopRunId = null;
            return;
        }

        // Clear the tool-activity card's parked "waiting" status now the decision is made — the
        // turn's own Complete() may be many tool calls away, and until then the card would keep
        // reporting a wait that is already over. Back to Running (an allowed tool proceeds; a
        // rejected/stopped turn ends and Complete() finalizes shortly after).
        if (message.OwnerToolActivity is { } activity
            && activity.Status is Models.ToolActivityStatus.WaitingForConfirmation
                or Models.ToolActivityStatus.WaitingForPermission)
        {
            activity.Status = Models.ToolActivityStatus.Running;
        }
    }

    /// <summary>Approving with no feedback sends the SDK's default "proceed" message;
    /// requesting changes with no feedback is a no-op.</summary>
    public async Task SubmitPlanReviewAsync(ChatMessage message, PlanReviewAction action)
    {
        var response = Services.Runtime.InteractiveResponseBuilder
            .BuildPlanReviewResponse(action, message.PlanFeedback);
        if (response is null)
        {
            message.InteractiveErrorText = LocalizedStrings.Get(
                "ChatDescribePlanChanges",
                "Describe the changes you want the agent to make.");
            return;
        }
        if (!message.TryBeginInteractiveSubmit()) return;

        var connection = CurrentConnection;
        if (connection is not { IsConnected: true })
        {
            message.FailInteractiveSubmit("Reconnect before reviewing this plan.", connectionLost: true);
            return;
        }

        var reservation = BeginInteractiveAnswer(
            response.Outcome, response.Rejected ? EventStatus.Error : EventStatus.Done);
        try
        {
            await connection.RespondPlanReviewAsync(response.Message);
            ConfirmInteractiveAnswer(reservation);
            message.CompleteInteractiveSubmit(response.Outcome, response.Rejected);
            RefreshPendingInteractive();
        }
        catch (Exception ex)
        {
            CancelInteractiveAnswer(reservation);
            message.FailInteractiveSubmit(ex.Message, connectionLost: !connection.IsConnected);
        }
    }

    /// <summary>
    /// Decodes/downloads an inbound image off the UI thread and caches it to disk so the
    /// message list and SQLite only ever hold a file path, never the base64 payload. Failure
    /// marks the attachment Failed rather than throwing.
    /// </summary>
    private async Task ResolveAgentImageAsync(string dataUrl, ChatAttachment attachment)
    {
        var resolved = await AttachmentImageCacheService
            .ResolveAndCacheAsync(dataUrl, _http)
            .ConfigureAwait(false);

        _dispatcher.TryEnqueue(() =>
        {
            if (resolved is { } value)
            {
                attachment.LocalCachePath = value.LocalPath;
                attachment.MimeType = value.MimeType;
                attachment.Status = AttachmentStatus.Sent;
            }
            else
            {
                attachment.Status = AttachmentStatus.Failed;
                attachment.Error = LocalizedStrings.Get(
                    "ChatImageCouldNotLoad",
                    "Image could not be loaded.");
            }
        });
    }
}
