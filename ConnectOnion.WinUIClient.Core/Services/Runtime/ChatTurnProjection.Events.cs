using System.Linq;
using System.Text.Json;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>
/// <see cref="ChatTurnProjection"/>: the stream-event dispatch table — one switch arm per wire
/// event type, mapping it to the bubble (or the mutation of an existing bubble) it produces.
///
/// This is the file to edit when the agent gains a new event type. It is kept apart from the
/// turn-completion logic in the main file because it is a flat, growing table rather than a
/// sequence: arms are independent of each other, and the ordering between them carries no
/// meaning. Everything it needs from the target goes through the small helpers at the bottom of
/// the main file, so nothing here touches the UI collection directly.
/// </summary>
public sealed partial class ChatTurnProjection
{
    private void ApplyParsed(AgentStreamEvent e, JsonElement root)
    {
        // An unparseable frame still projects its bubble (with empty fields), exactly as before —
        // the readers below tolerate a missing property but not an *undefined* element.
        if (root.ValueKind != JsonValueKind.Object) root = EmptyObject.RootElement;

        // Local kind-checked readers over the captured root. Locals rather than statics taking
        // the element, purely so the switch arms below read as `Str("model")` instead of
        // threading `root` through forty call sites; the closure is the whole point.
        string? Str(string name) => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        double? Num(string name) => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble() : null;
        bool? Flag(string name) => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var v)
                ? v.ValueKind == JsonValueKind.True ? true
                    : v.ValueKind == JsonValueKind.False ? false : (bool?)null
                : null;
        switch (e.Type)
        {
            // Local-only lifecycle markers. They intentionally render no separate bubble; the
            // successful marker is attached to the final OUTPUT message instead.
            case "interrupt_requested":
                _wasInterrupted = true;
                break;

            case "interrupt_request_failed":
                _wasInterrupted = false;
                break;

            // GetToolActivity creates the card on demand; tool_result uses `?.` instead, because
            // a result with no preceding call has nothing to attach to and must not conjure an
            // empty activity card out of a stray frame.
            case "tool_call":
                {
                    var step = GetToolActivity().ApplyCall(e, root);
                    if (IsWriteFileTool(step.ToolName)
                        && !_diffsByToolId.ContainsKey(step.Id)
                        && !_writeCallsAwaitingDiff.Contains(step.Id, StringComparer.Ordinal))
                    {
                        _writeCallsAwaitingDiff.Add(step.Id);
                    }
                    break;
                }

            case "tool_result":
                {
                    var step = _toolActivity?.ApplyResult(e, root);
                    if (step is not null && _diffsByToolId.TryGetValue(step.Id, out var diff))
                    {
                        diff.SetDiffState(step.Status switch
                        {
                            ToolStepStatus.Success => DiffChangeState.Applied,
                            ToolStepStatus.Warning => DiffChangeState.PartiallyApplied,
                            ToolStepStatus.Cancelled => DiffChangeState.Rejected,
                            _ => DiffChangeState.Failed,
                        });
                    }
                    break;
                }

            // llm_call/llm_result are what actually bracket the agent's thinking on this host —
            // there is no `thinking` frame in its trace stream, so nothing else opens a spinner.
            // The `thinking` arm below is kept for hosts that do emit reasoning text; when both
            // arrive, the text card wins and no bare indicator is added on top of it.
            case "llm_call":
                if (Str("model") is { } callModel) _turnModel = callModel;
                if (!_sawThinkingText) StartLlmThinking();
                break;

            case "llm_result":
                {
                    SettleLlmThinking();
                    if (Str("model") is { } resultModel) _turnModel = resultModel;

                    // Accumulating (+=), not assigning: one turn makes an LLM call per agent
                    // iteration, and the summary bubble reports the turn's total spend. The model,
                    // by contrast, is overwritten — the last one used is what gets labelled.
                    var (tokIn, tokOut) = TryParseRawTokenUsage(root);
                    if (tokIn is not null) { _turnTokensIn += tokIn.Value; _turnHasUsage = true; }
                    if (tokOut is not null) { _turnTokensOut += tokOut.Value; _turnHasUsage = true; }
                    if (Num("duration_ms") is { } dur) _turnDurationMs += dur;
                    if (Num("context_percent") is { } ctx) _turnContextPercent = ctx;
                    if (Num("tool_calls_count") is { } tcc) _turnToolCallsCount += (long)tcc;
                    // The only arm that moves these numbers, so it is the only one that publishes them.
                    // Announcing after every event instead would push an unchanged value dozens of
                    // times a turn for no reader.
                    ReportUsage();
                    break;
                }

            case "thinking":
                {
                    // Real reasoning text supersedes the bare llm_call spinner for the rest of the
                    // turn — a host that sends both must not stack an empty indicator on top.
                    _sawThinkingText = true;
                    SettleLlmThinking();
                    if (_currentThinking is null)
                    {
                        _currentThinking = new ChatMessage
                        {
                            Id = NextId(),
                            Role = ChatRole.Event,
                            EventKind = "activity",
                            EventTitle = "Thinking",
                            Status = EventStatus.Running,
                        };
                        _thinkingActivities.Add(_currentThinking);
                        Add(_currentThinking);
                    }
                    _currentThinking.AddThought(Str("content"));
                    break;
                }

            // intent / eval / compact share one shape: a "started" frame opens an activity row
            // keyed by the frame's id, and the matching completion frame mutates that same row
            // in place rather than adding a second one. A completion whose id never opened a row
            // (page joined mid-turn) is dropped — a lone "complete" reads as noise.
            case "intent":
                {
                    var id = Str("id");
                    if (Str("status") == "analyzing")
                    {
                        Add(NewActivity(id, "Understanding request...", EventStatus.Running));
                    }
                    else if (FindEvent(id, "activity") is { } existing)
                    {
                        existing.EventTitle = "Request understood";
                        existing.EventDetail = Str("ack");
                        existing.Status = EventStatus.Done;
                    }
                    break;
                }

            case "eval":
                {
                    var id = Str("id");
                    if (Str("status") == "evaluating")
                    {
                        var item = NewActivity(id, "Evaluating result...", EventStatus.Running);
                        item.EventDetail = Str("expected");
                        Add(item);
                    }
                    else if (FindEvent(id, "activity") is { } existing)
                    {
                        existing.EventTitle = "Evaluation complete";
                        existing.EventDetail = Str("summary") ?? Str("expected");
                        existing.Status = Flag("passed") == false ? EventStatus.Error : EventStatus.Done;
                    }
                    break;
                }

            case "compact":
                {
                    var id = Str("id");
                    if (Str("status") == "compacting")
                    {
                        Add(NewActivity(id, "Compacting context...", EventStatus.Running));
                    }
                    else if (FindEvent(id, "activity") is { } existing)
                    {
                        existing.EventTitle = "Context compacted";
                        existing.Status = Str("status") == "error" ? EventStatus.Error : EventStatus.Done;
                        existing.EventDetail = Str("message") ?? Str("error");
                        if (Num("context_before") is { } before && Num("context_after") is { } after)
                            existing.EventMeta = $"{before:0} → {after:0}";
                    }
                    break;
                }

            case "tool_blocked":
                GetToolActivity().AddBlocked(e, root);
                break;

            case "agent_image":
                {
                    // Don't render the image yet — buffer it and hang it on the final output when the
                    // turn completes (see FlushPendingImages). Rendering here would drop the image in
                    // above the tool activity, usage summary, and final reply that stream in after it.
                    if (Str("cached_path") is { Length: > 0 } cachedPath)
                    {
                        _pendingAgentImages.Add(new PendingAgentImage(cachedPath, Str("mime_type"), true));
                    }
                    else if (AttachmentWireEvents.TryGetAgentImageDataUrl(WireMessage.Wrap(root), out var dataUrl))
                    {
                        _pendingAgentImages.Add(new PendingAgentImage(dataUrl, null, false));
                    }
                    break;
                }

            case "files_received":
                {
                    var names = new List<string>();
                    if (AttachmentWireEvents.TryGetFilesReceived(WireMessage.Wrap(root), out var files))
                    {
                        foreach (var file in files) names.Add(file.Name);
                    }
                    var item = NewActivity(null, $"Files received - {names.Count} file(s)", EventStatus.Done);
                    if (names.Count > 0) item.EventDetail = string.Join(", ", names);
                    Add(item);
                    break;
                }

            // Client-originated connection lifecycle. Published by AgentSessionManager, not the
            // host — the socket is down when the first of these fires, so nothing could have
            // sent it. They share one row keyed "reconnect": the attempts overwrite each other
            // rather than stacking five rows, and the outcome settles the same row.
            case "reconnecting":
                {
                    var attempt = Num("attempt") ?? 1;
                    var max = Num("max_attempts") ?? 5;
                    if (FindEvent("reconnect", "activity") is { } open)
                    {
                        open.EventTitle = $"Reconnecting ({attempt:0}/{max:0})";
                        open.Status = EventStatus.Running;
                    }
                    else
                    {
                        var item = NewActivity("reconnect", $"Reconnecting ({attempt:0}/{max:0})", EventStatus.Running);
                        item.EventDetail = Str("reason");
                        Add(item);
                    }
                    break;
                }

            case "reconnected":
                {
                    var resumed = Flag("resumed") == true;
                    if (!resumed) CompleteOpenDiffs(DiffChangeState.Disconnected);
                    if (FindEvent("reconnect", "activity") is { } open)
                    {
                        // "Reconnected" alone would be misleading when the turn did not survive:
                        // the socket is back either way, but only a host still running the agent
                        // means the reply is still coming.
                        open.EventTitle = resumed ? "Reconnected - resuming" : "Reconnected";
                        open.EventDetail = resumed ? null : "The agent was no longer running this turn.";
                        open.Status = EventStatus.Done;
                    }
                    break;
                }

            // A proposed file write, sent by DiffWriter purely so the UI can show the diff. It
            // does NOT block and carries no options — the approve/reject choice arrives right
            // behind it as an ordinary ask_user (`_ask_approval` in diff_writer.py), which the
            // ask_user card already renders. So this card is read-only on purpose: putting
            // buttons here would answer a frame nothing is waiting on, and then the real
            // ask_user would block forever.
            case "diff_preview":
                {
                    var path = Str("path") ?? "(unknown path)";
                    var card = new ChatMessage
                    {
                        Id = NextId(),
                        Role = ChatRole.Event,
                        EventKind = "diff_preview",
                        EventTitle = path,
                        // The unified-diff body. Empty means the writer found nothing to change —
                        // worth saying out loud, because a silent empty card reads as a failure.
                        EventDetail = Str("preview") is { Length: > 0 } p ? p : "(no changes)",
                        // file_exists distinguishes "new file" (all additions) from "modified".
                        EventEyebrow = Flag("file_exists") == true ? "MODIFY" : "CREATE",
                        Status = EventStatus.Done,
                    };
                    if (_writeCallsAwaitingDiff.Count > 0)
                    {
                        var toolId = _writeCallsAwaitingDiff[^1];
                        _writeCallsAwaitingDiff.RemoveAt(_writeCallsAwaitingDiff.Count - 1);
                        _diffsByToolId[toolId] = card;
                        card.SetDiffState(DiffChangeState.Pending);
                    }
                    else
                    {
                        // remote_diff_preview is informational and has no approval/result contract.
                        card.SetDiffState(DiffChangeState.Preview);
                    }
                    // Note the `truncated` flag is deliberately NOT surfaced as a separate badge:
                    // _truncate_preview already appends a literal "\n...(truncated)" line to the
                    // preview body it sends, so a badge would state the same fact twice.
                    Add(card);
                    break;
                }

            case "assistant":
                {
                    var content = Str("content");
                    if (!string.IsNullOrEmpty(content))
                    {
                        CompleteThinkingActivities(EventStatus.Done);
                        _streamedFinalReply = new ChatMessage
                        {
                            Id = NextId(),
                            Role = ChatRole.Agent,
                            Content = content,
                            AgentName = AgentName,
                        };
                        Add(_streamedFinalReply);
                    }
                    break;
                }

            case "ONBOARD_SUCCESS":
                // Seal the invite-code card: the code was accepted, so its input must stop
                // offering a second submission. Sealing it here (not just in the view model
                // that clicked Submit) also covers the auto-submitted and replayed cases.
                foreach (var card in Messages)
                {
                    if (!card.IsOnboarding) continue;
                    // Clearing IsOnboarding retires the card for good: the input row is gone,
                    // and a later failed run in this conversation won't mistake an already
                    // accepted code for one still awaiting an answer. IsOnboardingSubmitted is
                    // what raises the change notification (IsOnboarding is a plain property),
                    // so both have to move.
                    card.IsOnboardingSubmitted = true;
                    card.IsOnboarding = false;
                    card.Content = "Invite code accepted.";
                }
                Add(NewActivity(null, "Onboarding complete", EventStatus.Done));
                break;

            case "mode_changed":
                {
                    // The agent can enter plan mode by itself (triggered_by: "agent"), so this card is
                    // not an echo of the user's own click — it is the only place a mode the *agent*
                    // chose becomes visible.
                    var mode = Str("mode");
                    var item = NewActivity(null, $"Mode: {AgentModes.DisplayName(mode)}", EventStatus.Done);
                    item.EventMeta = Str("triggered_by") switch
                    {
                        "agent" => "by agent",
                        "ulw_checkpoint" => "by checkpoint",
                        _ => null,
                    };
                    // What the switch means for the user, which the mode's name alone does not say —
                    // someone who has never seen plan mode has no way to know a review is coming, or
                    // that nothing will change until they approve it.
                    //
                    // Stated in the third person about the protocol, deliberately. The reference web
                    // client fills this space with invented first-person prose ("I'm designing a
                    // detailed implementation plan…"), which reads as the agent speaking when the
                    // client wrote it — the agent said nothing of the kind.
                    item.EventDetail = mode == AgentModes.Plan
                        ? "The agent will research first and send a plan for approval before making changes."
                        : null;
                    Add(item);
                    break;
                }

            // Authoritative transport/session state. The live view model consumes its mode,
            // while the transcript remains free of technical session JSON.
            case "session_sync":
                break;

            // ---- interactive turns & onboarding (synthetic events, see AgentTurnExecutor) ----

            case "ONBOARD_REQUIRED":
                // One live invite card at a time. The agent re-sends this on every reconnect
                // attempt, and the persist path replays it, so without the guard a user who
                // hasn't answered yet would accumulate a column of identical input boxes.
                if (!Messages.Any(m => m.IsOnboarding && !m.IsOnboardingSubmitted))
                {
                    // The gate says what it accepts. Before this was read, the card always
                    // demanded an invite code — so an agent gated on payment showed a box for a
                    // code that does not exist and could never be got past.
                    var gate = AgentInteractiveParsers.ParseOnboard(WireMessage.Wrap(root));
                    Add(new ChatMessage
                    {
                        Id = NextId(),
                        Role = ChatRole.Agent,
                        AgentName = AgentName,
                        IsOnboarding = true,
                        OnboardRequest = gate,
                        Content = gate.AcceptsPayment && !gate.AcceptsInviteCode
                            ? "This agent requires a payment before the conversation can continue."
                            : gate.AcceptsPayment
                                ? "This agent requires an invite code or a payment before the conversation can continue."
                                : "This agent requires an invite code before the conversation can continue.",
                    });
                }
                break;

            case "ask_user":
                {
                    var request = AgentInteractiveParsers.ParseAskUser(WireMessage.Wrap(root));
                    // Deduped by the request's own id, the same way the intent/eval arms above are.
                    // The host replays frames the client never acknowledged (CONNECT carries
                    // last_msg_id), and every replay re-raises AskUserRequested — so a reconnect
                    // during an unanswered question would otherwise stack a second card carrying a
                    // second copy of the options. The buffered events are replayed into the persist
                    // projection as well, so the duplicate outlives the session that caused it.
                    if (FindEvent(request.Id, "ask_user") is { } existingAsk)
                    {
                        existingAsk.RevalidateInteractiveRequest();
                        AttachPendingImages(existingAsk);
                        break;
                    }

                    var message = new ChatMessage
                    {
                        Id = NextId(),
                        Role = ChatRole.Event,
                        EventKind = "ask_user",
                        EventKey = request.Id,
                        EventEyebrow = "Question",
                        EventTitle = request.Text,
                        Status = EventStatus.Running,
                    };
                    message.AskUserMultiSelect = request.MultiSelect;
                    foreach (var option in request.Options)
                    {
                        // Entries back both modes. Single-select used to bind a RadioButtons control
                        // straight to the raw strings, which meant the option card's chrome was
                        // whatever the default RadioButton template happened to render. Going through
                        // entries lets one hand-built card serve both, so the two modes cannot drift.
                        message.AskUserOptionEntries.Add(new AskUserOptionEntry { Text = option, Owner = message });
                    }
                    foreach (var field in request.Fields)
                    {
                        message.AskUserFields.Add(new AskUserFieldEntry
                        {
                            Name = field.Name,
                            Label = field.Label,
                            Placeholder = field.Placeholder,
                            Required = field.Required,
                            Type = field.Type,
                            Owner = message,
                        });
                    }
                    if (message.IsFileChangeApproval)
                    {
                        message.AttachRelatedDiffPreview(Messages.LastOrDefault(candidate =>
                            candidate.IsDiffPreviewEvent
                            && candidate.DiffState is DiffChangeState.Pending or DiffChangeState.Preview
                            && string.Equals(candidate.EventTitle, message.AskUserTargetPath,
                                StringComparison.OrdinalIgnoreCase)));
                    }
                    Add(message);
                    // A QR/login image immediately before ask_user is part of the question. Waiting
                    // until OUTPUT would deadlock the UX: the user cannot scan what is still hidden.
                    AttachPendingImages(message);
                    break;
                }

            case "approval_needed":
                {
                    // The approval renders inside the turn's ONE tool-activity card, which aggregates
                    // every tool in the turn as a step and collapses under a single "Tool activity"
                    // header when it finishes. Approvals in a turn are sequential — the agent blocks on
                    // each before the next — so one Approval slot suffices: it always holds the single
                    // currently-pending decision, while every already-answered tool sits as a done step
                    // above it. GetToolActivity() creates the card only if no tool_call opened one yet;
                    // it never adds a duplicate step.
                    var toolActivity = GetToolActivity();
                    toolActivity.WaitForConfirmation();

                    var argsJson = root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("arguments", out var a)
                            ? a.GetRawText() : "{}";
                    var toolName = Str("tool") ?? "";
                    var toolDisplayName = ToolActivityProjector.DisplayName(toolName);
                    var batchSummary = "";
                    if (root.TryGetProperty("batch_remaining", out var batch)
                        && batch.ValueKind == JsonValueKind.Array)
                    {
                        var tools = batch.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.Object
                                && item.TryGetProperty("tool", out var tool)
                                && tool.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetProperty("tool").GetString()!)
                            .ToList();
                        if (tools.Count > 0)
                            batchSummary = $"This batch also includes {tools.Count}: {string.Join(", ", tools)}";
                    }

                    // A reconnect can replay the pending approval frame. Keep the existing live card
                    // instead of appending another hidden approval message: those hidden rows still
                    // participate in ResolveInteractiveCards' FIFO, so duplicating one would consume
                    // the answer intended for the next interactive card in the turn.
                    if (toolActivity.Activity.Approval is { IsApprovalPending: true } pending
                        && string.Equals(pending.EventTitle, toolDisplayName, StringComparison.Ordinal)
                        && string.Equals(pending.EventArgs, PrettyJson(argsJson), StringComparison.Ordinal))
                    {
                        break;
                    }

                    var approval = new ChatMessage
                    {
                        Id = NextId(),
                        Role = ChatRole.Event,
                        EventKind = "approval",
                        EventEyebrow = "Approval needed",
                        EventTitle = toolDisplayName,
                        EventDetail = Str("reason") ?? Str("description"),
                        EventArgs = PrettyJson(argsJson),
                        Status = EventStatus.Running,
                        IsEmbeddedApproval = true,
                        // Extracted target/operation for the card's summary — never the raw args blob.
                        ApprovalTargetInfo = ApprovalTargetFormatter.Extract(argsJson),
                        ApprovalBatchSummary = batchSummary,
                        // Back-ref so answering can clear the card's parked status immediately.
                        OwnerToolActivity = toolActivity.Activity,
                    };
                    // Link it into the card, and still Add it to the list: approval messages always
                    // render Empty because ToolActivityView is their sole UI, but the message must remain
                    // in the list so the persist path's answer alignment (ResolveInteractiveCards, which
                    // dequeues answers in creation order across ask_user/approval/plan_review) stays correct.
                    toolActivity.Activity.Approval = approval;
                    Add(approval);
                    break;
                }

            case "plan_review":
                {
                    // A resumed session replays its pending plan. Treat that replay as the sync signal
                    // that makes a connection-lost card actionable again, never as a second card.
                    var planContent = Str("plan_content") ?? "";
                    if (_planReviewCards.TryGetValue(planContent, out var existingPlan))
                    {
                        existingPlan.RevalidateInteractiveRequest();
                        break;
                    }
                    var planCard = new ChatMessage
                    {
                        Id = NextId(),
                        Role = ChatRole.Event,
                        EventKind = "plan_review",
                        EventEyebrow = "Plan review",
                        EventTitle = "Review the plan",
                        EventDetail = planContent,
                        Status = EventStatus.Running,
                    };
                    _planReviewCards.Add(planContent, planCard);
                    Add(planCard);
                    break;
                }
        }
    }
}
