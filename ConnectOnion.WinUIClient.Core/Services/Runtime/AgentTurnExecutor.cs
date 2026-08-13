using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ConnectOnion.Protocol;
using ConnectOnion.Protocol.Runtime;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>
/// Drives a single turn against an app-owned <see cref="AgentConnectionService"/> and
/// reports progress into the run's <see cref="IRunSink"/>. Interactive turns and
/// onboarding — which the connection surfaces as dedicated typed events, not generic
/// stream events — are re-serialized into synthetic stream frames and buffered too, so a
/// page created mid-turn replays them into the exact same bubbles. Answering those turns
/// is done separately by the view model on the same socket (fetched from the connection
/// registry); this executor only records that they happened.
/// </summary>
public sealed class AgentTurnExecutor : ITurnExecutor
{
    private readonly AgentConnectionService _connection;
    private readonly Action<ApprovalRequest>? _onApproval;
    private readonly Action<string>? _onInteractive;
    private readonly Func<AgentStreamEvent, Task<AgentStreamEvent>>? _prepareEvent;
    private readonly bool _resumeExisting;

    /// <param name="onApproval">App-level hook fired when the agent requests approval, so the
    /// notification is raised once (in the tray, across windows) rather than per open page.</param>
    /// <param name="onInteractive">Fired with the kind ("approval"/"ask_user"/"plan_review") of
    /// every interactive turn the agent opens. The session manager tracks the open one because
    /// it decides what Stop can actually do: an agent parked on an approval can be halted for
    /// real, one parked on a question can only be told to stop, and one mid-LLM-call can only be
    /// asked nicely.</param>
    /// <param name="resumeExisting">Attach to a turn the host is already running instead of
    /// sending an INPUT. Used by the resume-on-open probe after an app restart: every event
    /// handler below is wired identically, so the replayed turn projects into exactly the same
    /// cards a live one does — only the frame that starts it differs.</param>
    public AgentTurnExecutor(
        AgentConnectionService connection,
        Action<ApprovalRequest>? onApproval = null,
        Action<string>? onInteractive = null,
        Func<AgentStreamEvent, Task<AgentStreamEvent>>? prepareEvent = null,
        bool resumeExisting = false)
    {
        _connection = connection;
        _onApproval = onApproval;
        _onInteractive = onInteractive;
        _prepareEvent = prepareEvent;
        _resumeExisting = resumeExisting;
    }

    public async Task<string> ExecuteAsync(TurnRequest request, IRunSink sink)
    {
        var eventGate = new object();
        Task eventTail = Task.CompletedTask;

        void QueueEvent(AgentStreamEvent e)
        {
            sink.SetRunning();
            lock (eventGate)
            {
                eventTail = eventTail.ContinueWith(
                        async _ =>
                        {
                            var prepared = _prepareEvent is null ? e : await _prepareEvent(e).ConfigureAwait(false);
                            sink.Publish(prepared);
                            if (prepared.Type == "assistant")
                            {
                                var content = ExtractString(prepared.RawJson, "content");
                                if (!string.IsNullOrEmpty(content)) sink.UpdatePartial(content!);
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default)
                    .Unwrap();
            }
        }

        void OnInputSent() => sink.SetRunning();
        void OnStream(AgentStreamEvent e) => QueueEvent(e);
        void OnOnboard(OnboardRequest r) => QueueEvent(new AgentStreamEvent(
            "ONBOARD_REQUIRED", "Agent requires onboarding", null, BuildOnboardFrame(r)));
        void OnAsk(AskUserRequest r)
        {
            QueueEvent(new AgentStreamEvent("ask_user", r.Text, r.Id, BuildAskUserFrame(r)));
            _onInteractive?.Invoke("ask_user");
        }
        void OnApproval(ApprovalRequest r)
        {
            QueueEvent(new AgentStreamEvent("approval_needed", r.Tool, null, BuildApprovalFrame(r)));
            _onInteractive?.Invoke("approval");
            _onApproval?.Invoke(r);
        }
        void OnPlan(PlanReviewRequest r)
        {
            QueueEvent(new AgentStreamEvent("plan_review", "Plan review", null, BuildPlanFrame(r)));
            _onInteractive?.Invoke("plan_review");
        }

        _connection.InputSent += OnInputSent;
        _connection.StreamEvent += OnStream;
        _connection.OnboardRequired += OnOnboard;
        _connection.AskUserRequested += OnAsk;
        _connection.ApprovalRequested += OnApproval;
        _connection.PlanReviewRequested += OnPlan;
        try
        {
            sink.SetConnecting();
            var images = request.Images;
            var files = request.Files;
            if (!_resumeExisting && request.AttachmentSources is { Count: > 0 })
            {
                var encoded = await AgentSessionManager
                    .EncodeAttachmentSourcesAsync(request.AttachmentSources)
                    .ConfigureAwait(false);
                images = encoded.Images;
                files = encoded.Files;
            }
            // Resuming deliberately sends nothing: the agent is mid-turn and an INPUT here would
            // reach the host as a RUNTIME_INPUT — an extra prompt the user never typed.
            string reply;
            if (_resumeExisting)
            {
                reply = await _connection.ResumeRunningSessionAsync(
                        request.SessionId!, sink.CancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var completion = await _connection.BeginInputAsync(
                        request.Prompt, request.SessionId, images, files, request.Mode,
                        sink.CancellationToken)
                    .ConfigureAwait(false);
                // The INPUT frame is now on the socket. Drop its large encoded representation
                // before awaiting OUTPUT; retry retains only request.AttachmentSources.
                images = null;
                files = null;
                reply = await completion.ConfigureAwait(false);
            }
            Task pendingEvents;
            lock (eventGate) pendingEvents = eventTail;
            await pendingEvents.ConfigureAwait(false);
            if (!string.IsNullOrEmpty(reply)) sink.UpdatePartial(reply);
            return reply;
        }
        finally
        {
            _connection.InputSent -= OnInputSent;
            _connection.StreamEvent -= OnStream;
            _connection.OnboardRequired -= OnOnboard;
            _connection.AskUserRequested -= OnAsk;
            _connection.ApprovalRequested -= OnApproval;
            _connection.PlanReviewRequested -= OnPlan;
        }
    }

    private static string? ExtractString(string rawJson, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;
        }
        catch { return null; }
    }

    // Re-serialize typed interactive requests back into the canonical wire shape the
    // projection parses (keys chosen to round-trip through the same reader).

    private static string BuildAskUserFrame(AskUserRequest r)
    {
        var frame = new Dictionary<string, object?>
        {
            ["type"] = "ask_user",
            ["id"] = r.Id,
            ["text"] = r.Text,
            ["options"] = r.Options,
            ["multi_select"] = r.MultiSelect,
            ["fields"] = r.Fields.Select(f => new Dictionary<string, object?>
            {
                ["name"] = f.Name,
                ["label"] = f.Label,
                ["placeholder"] = f.Placeholder,
                ["required"] = f.Required,
                ["type"] = f.Type,
            }).ToList(),
        };
        return WireJson.Serialize(frame);
    }

    /// <summary>Re-serializes the onboarding gate so the projection sees the same shape whether it
    /// is reading the live frame or replaying this one. The keys are the host's own
    /// (<c>methods</c>/<c>payment_amount</c>/<c>payment_address</c>) so
    /// <c>AgentInteractiveParsers.ParseOnboard</c> round-trips it.</summary>
    private static string BuildOnboardFrame(OnboardRequest r)
        => WireJson.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "ONBOARD_REQUIRED",
            ["methods"] = r.Methods,
            ["payment_amount"] = r.PaymentAmount,
            ["payment_address"] = r.PaymentAddress,
        });

    private static string BuildApprovalFrame(ApprovalRequest r)
    {
        // JsonDocument.Parse rather than Deserialize<JsonElement>: same result, no reflection
        // (see docs/TRIMMING.md). Clone, because the element does not outlive its document.
        var args = ParseElement(r.ArgumentsJson) ?? ParseElement("{}")!.Value;
        var frame = new Dictionary<string, object?>
        {
            ["type"] = "approval_needed",
            ["tool"] = r.Tool,
            ["description"] = r.Description,
            ["arguments"] = args,
            ["reason"] = r.Reason,
        };
        if (!string.IsNullOrWhiteSpace(r.BatchRemainingJson))
        {
            // Optional preview data must not hide the approval itself, so a parse failure
            // simply leaves the key off.
            if (ParseElement(r.BatchRemainingJson) is { } remaining)
                frame["batch_remaining"] = remaining;
        }
        return WireJson.Serialize(frame);
    }

    private static JsonElement? ParseElement(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private static string BuildPlanFrame(PlanReviewRequest r)
        => WireJson.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "plan_review",
            ["plan_content"] = r.PlanContent,
        });
}
