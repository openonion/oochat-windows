using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ConnectOnion.Protocol;
using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.Services.Runtime;

/// <summary>
/// Pure UI projection for raw tool stream frames. It owns correlation, result
/// classification, display-name/target mapping and sensitive-data redaction; views only bind
/// to the resulting activity. A result is never matched by tool name.
/// </summary>
public sealed partial class ToolActivityProjector
{
    /// <summary>Argument/result JSON keys whose <i>value</i> is replaced wholesale by
    /// 
    /// <c>[hidden]</c>. Matched by exact (case-insensitive) property name, so this only
    /// catches structured payloads — free-form text goes through <see cref="SanitizeText"/>
    /// instead, and the two are deliberately both applied.</summary>
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "cookie", "set-cookie", "token", "access_token", "refresh_token",
        "api_key", "apikey", "password", "secret", "client_secret", "private_key",
    };
    /// <summary>Substrings that mean "the tool ran, but the answer is not usable" — the step
    /// is shown as a warning rather than an error, because the agent will usually carry on.</summary>
    private static readonly string[] WarningPatterns =
    {
        "access denied", "permission denied", "unauthorized", "forbidden",
        "empty result", "no content", "captcha", "rate limit", "redirect failed",
    };
    /// <summary>
    /// HTTP status codes that mean the same as the warning phrases above, matched on word
    /// boundaries rather than as bare substrings.
    ///
    /// <para>They cannot live in <see cref="WarningPatterns"/>: result text routinely contains
    /// URLs whose query strings and hex ids embed those three digits, and a plain
    /// <c>Contains("403")</c> paints a perfectly successful step amber. The case that prompted
    /// this was a browser navigation reporting
    /// <c>…&amp;rdrig=750A8A63BA3A403A8733EAD7629913CE</c> — the "403" is inside a redirect
    /// tracking id, so the card said "warning" while every word a user could read said success.
    /// Same reasoning as the leading space on <c>" error"</c> below.</para>
    /// </summary>
    [GeneratedRegex(@"\b(?:403|404)\b", RegexOptions.CultureInvariant)]
    private static partial Regex HttpProblemCodes { get; }

    /// <summary>403 alone, for the one caller that distinguishes it from 404. Kept separate from
    /// <see cref="HttpProblemCodes"/> rather than reusing it: that one also matches 404, and
    /// <see cref="FriendlyProblem"/> would then report a missing page as "Page access denied".</summary>
    [GeneratedRegex(@"\b403\b", RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenCode { get; }

    /// <summary>Labelled credentials in free-form text — <c>token: abc</c>, <c>password=hunter2</c>.
    /// <para><see cref="RegexOptions.CultureInvariant"/> matters on a case-insensitive match of
    /// ASCII keywords: without it the comparison follows the current culture, and under tr-TR
    /// <c>I</c> does not case-fold to <c>i</c>, so <c>API_KEY=…</c> would go unredacted on a
    /// Turkish machine.</para></summary>
    [GeneratedRegex(
        @"(authorization|cookie|token|api[_-]?key|password|secret)\s*[:=]\s*([^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LabelledSecrets { get; }
    /// <summary>Substrings that mean the tool call itself did not complete. Checked before the
    /// warning list, so an overlapping result classifies as the more severe of the two. Note
    /// the leading space on <c>" error"</c>: it keeps this off words that merely contain the
    /// letters (stderr, terrorism, no_errors) at the cost of missing a result that opens with it.</summary>
    private static readonly string[] FailurePatterns =
    {
        "timeout", "connection refused", "failed", " error", "exception", "network error",
    };
    /// <summary>Tool-name substrings that earn a step the high-risk marker in the UI. This is a
    /// presentation hint only — it does not gate execution (the agent's own approval flow does
    /// that) — so a false positive costs nothing but a badge, which is why the list is broad.</summary>
    private static readonly string[] HighRiskTokens =
    {
        "send", "delete", "write", "remove", "command", "execute", "upload", "download",
        "payment", "purchase", "submit", "permission", "account", "database",
    };
    // Argument names probed, in order, to work out what a step acted *on* (see DisplayTarget).
    private static readonly string[] UrlPropertyNames = { "url", "uri", "href" };
    private static readonly string[] PathPropertyNames = { "path", "file", "file_path", "filename" };

    public ToolActivityViewModel Activity { get; }

    private readonly bool _isLiveView;

    /// <param name="isLiveView">Whether a user is watching this card right now. Defaults to false
    /// so the headless persistence pass and the legacy-row migration can construct a projector
    /// without opting into interruption.</param>
    public ToolActivityProjector(ToolActivityViewModel activity, bool isLiveView = false)
    {
        Activity = activity;
        _isLiveView = isLiveView;
    }

    /// <summary>
    /// The card's auto-expansion rule: <b>open while the tools are running, folded once they are
    /// done</b>. A turn in progress is when the timeline is worth watching; a finished one has a
    /// header that already says what happened, and leaving it open pushes the agent's actual
    /// reply down the page on every tool-using turn.
    ///
    /// Two states keep the card open past completion, and both are cases where the detail is the
    /// whole point: something failed, or the turn is blocked waiting on a decision.
    ///
    /// Gated on <paramref name="_isLiveView"/> so the headless persistence pass writes the card
    /// collapsed: reopening an old conversation should not replay every past run at full height.
    ///
    /// Two things outrank it. <see cref="ToolActivityViewModel.IsDetailed"/> is an explicit
    /// "always show me detail" setting. And
    /// <see cref="ToolActivityViewModel.HasUserExpansionOverride"/> means the user has already
    /// said what they want for this card — after that, auto-expansion stops touching it, so
    /// folding a noisy card mid-turn is not undone by the next tool that starts.
    /// </summary>
    private void ApplyAutoExpansion()
    {
        if (Activity.IsDetailed)
        {
            Activity.IsExpanded = true;
            return;
        }
        if (Activity.HasUserExpansionOverride) return;

        var failed = Activity.Status == ToolActivityStatus.Failed
            || Activity.Steps.Any(step => step.Status == ToolStepStatus.Failed);
        var awaitingUser = Activity.Status
            is ToolActivityStatus.WaitingForConfirmation
            or ToolActivityStatus.WaitingForPermission;
        var running = Activity.Status == ToolActivityStatus.Running;

        // Awaiting the user COLLAPSES, and this is the opposite of what it used to do.
        //
        // Expanding on approval looked right — "show them what the tool was about to do" — but the
        // decision controls sit at the *bottom* of the card, below every step the turn has already
        // taken. A turn that had run eight tools produced a card over a thousand pixels tall whose
        // last few hundred pixels were the only actionable part, so the thing the agent was blocked
        // on was pushed off the bottom of the viewport by its own history. Collapsing puts the
        // header, the command, the risk line and the buttons together in about a third of that.
        //
        // Nothing needed to answer is lost: the header names the tool and carries the "Approval
        // required" badge, the approval section carries the command and its risk, and the step
        // count stays on the header (CollapsedStepCountHint). The timeline is context, one click
        // away, and a user who opens it keeps it open — HasUserExpansionOverride is checked above.
        //
        // It also removes a layout jolt. The card used to grow by hundreds of pixels at the moment
        // an approval arrived, shoving the transcript under anyone who was not pinned to the
        // bottom; now it shrinks instead.
        Activity.IsExpanded = _isLiveView && !awaitingUser && (running || failed);
    }

    /// <summary>Projects a <c>tool_call</c> frame into a new running step (or returns the
    /// existing one if this frame has already been seen).</summary>
    public ToolStepViewModel ApplyCall(AgentStreamEvent e)
    {
        using var doc = JsonDocument.Parse(e.RawJson);
        return ApplyCall(e, doc.RootElement);
    }

    internal ToolStepViewModel ApplyCall(AgentStreamEvent e, JsonElement root)
    {
        var toolName = String(root, "name") ?? String(root, "tool") ?? "tool";
        // A call with no id still gets a step — the user should see that *something* ran — but
        // the synthetic id is intentionally unmatchable, so a later result cannot bind to it.
        var id = CorrelationId(root, e) ?? $"unmatched-call-{Activity.Steps.Count + 1}";
        var existing = Activity.Steps.FirstOrDefault(step => step.Id == id);
        if (existing is not null) return existing; // duplicate stream frame / page replay

        var args = ObjectJson(root, "args") ?? ObjectJson(root, "arguments") ?? "{}";
        var step = new ToolStepViewModel
        {
            Id = id,
            Sequence = Activity.Steps.Count + 1,
            ToolName = toolName,
            DisplayName = DisplayName(toolName),
            DisplayTarget = DisplayTarget(toolName, args),
            Arguments = SanitizeJson(args),
            StartedAt = Timestamp(root) ?? DateTimeOffset.UtcNow,
            Status = ToolStepStatus.Running,
            IsHighRisk = IsHighRisk(toolName),
        };
        Activity.Steps.Add(step);
        Activity.Status = ToolActivityStatus.Running;
        Activity.Summary = RunningSummary(step);
        // Opens the card so a running turn shows its timeline. Safe to do per call only because
        // ApplyAutoExpansion bails on HasUserExpansionOverride: without that guard this line is
        // the old bug where a card the user folded mid-turn sprang back open on the next tool.
        ApplyAutoExpansion();
        return step;
    }

    /// <summary>Folds a <c>tool_result</c> frame into the step it belongs to. Returns null when
    /// the result cannot be correlated (no id, or an id for a call this activity never saw) —
    /// dropping it is correct, because attaching a result to the wrong step is worse than
    /// showing none.</summary>
    public ToolStepViewModel? ApplyResult(AgentStreamEvent e)
    {
        using var doc = JsonDocument.Parse(e.RawJson);
        return ApplyResult(e, doc.RootElement);
    }

    internal ToolStepViewModel? ApplyResult(AgentStreamEvent e, JsonElement root)
    {
        var id = CorrelationId(root, e);
        if (string.IsNullOrWhiteSpace(id)) return null; // never guess from the tool name
        var step = Activity.Steps.FirstOrDefault(item => item.Id == id);
        if (step is null) return null;

        // Capped before anything else touches it. A tool result is unbounded — a page scrape or a
        // file read is routinely hundreds of KB — and this string is retained for the life of the
        // conversation (in the message list, in ConversationCache, and serialized into the
        // message's event_args row). The card renders it in a 180px scroller, so nobody is reading
        // past this; the untruncated payload is already in trace_events if it's ever needed.
        var result = Cap(String(root, "result") ?? String(root, "message") ?? String(root, "error") ?? "");
        var transportStatus = String(root, "status");
        // The step's own tool name, not the frame's: the result frame does not always carry one,
        // and the step was correlated by id anyway.
        var classification = Classify(transportStatus, result, step.ToolName);
        step.Status = classification.Status;
        step.Result = string.IsNullOrWhiteSpace(result) ? null : SanitizeText(result);
        step.Error = classification.Status == ToolStepStatus.Failed ? SanitizeText(result) : null;
        step.Summary = classification.Summary;
        step.CompletedAt = Timestamp(root) ?? DateTimeOffset.UtcNow;
        // Prefer the agent's own measurement; the wall-clock fallback also counts the time the
        // frame spent in transit, so it is an upper bound rather than the tool's real cost.
        step.DurationMs = Number(root, "timing_ms") ?? Number(root, "duration_ms")
            ?? Math.Max(0, (step.CompletedAt.Value - step.StartedAt).TotalMilliseconds);
        // Tools can overlap, so the header keeps naming whatever is *still* running; falling
        // back to this step covers the case where that was the last one outstanding.
        Activity.Summary = RunningSummary(Activity.Steps.LastOrDefault(item => item.Status == ToolStepStatus.Running) ?? step);
        Activity.RefreshPresentation();
        return step;
    }

    /// <summary>Projects a <c>tool_blocked</c> frame: a step that was refused before it ran, so
    /// it is born terminal (started and completed at the same instant, no result to wait for)
    /// and forces the card open — a silently blocked action is exactly what a user needs to see.
    /// The blocked <c>command</c> rides in <c>Result</c> so the detail pane shows what was refused.</summary>
    public ToolStepViewModel AddBlocked(AgentStreamEvent e)
    {
        using var doc = JsonDocument.Parse(e.RawJson);
        return AddBlocked(e, doc.RootElement);
    }

    internal ToolStepViewModel AddBlocked(AgentStreamEvent e, JsonElement root)
    {
        var toolName = String(root, "tool") ?? String(root, "name") ?? "tool";
        var step = new ToolStepViewModel
        {
            Id = CorrelationId(root, e) ?? $"blocked-{Activity.Steps.Count + 1}",
            Sequence = Activity.Steps.Count + 1,
            ToolName = toolName,
            DisplayName = DisplayName(toolName),
            DisplayTarget = DisplayTarget(toolName, ObjectJson(root, "args") ?? "{}"),
            Status = ToolStepStatus.Failed,
            Summary = FriendlyProblem(String(root, "message") ?? String(root, "reason") ?? "Tool action blocked"),
            Error = SanitizeText(String(root, "message") ?? String(root, "reason") ?? "Tool action blocked"),
            Result = SanitizeText(String(root, "command") ?? ""),
            IsHighRisk = IsHighRisk(toolName),
            StartedAt = Timestamp(root) ?? DateTimeOffset.UtcNow,
            CompletedAt = Timestamp(root) ?? DateTimeOffset.UtcNow,
        };
        Activity.Steps.Add(step);
        Activity.Status = ToolActivityStatus.Failed;
        Activity.ErrorSummary = step.Summary;
        Activity.Summary = $"Failed · view details";
        ApplyAutoExpansion();
        return step;
    }

    /// <summary>Parks the card while the agent waits on the user, and opens it on a live view:
    /// the turn cannot proceed until they act, and what the tool was about to do is exactly what
    /// the decision hinges on.</summary>
    public void WaitForConfirmation(bool permission = false)
    {
        Activity.Status = permission ? ToolActivityStatus.WaitingForPermission : ToolActivityStatus.WaitingForConfirmation;
        // Deliberately does NOT overwrite Summary with "Waiting…": the embedded approval section
        // carries the waiting state, the header summary is hidden while awaiting, and once the
        // decision resolves the summary should read as the normal running/completed one rather than
        // a stale "Waiting for your confirmation…" that the card's Complete hasn't cleared yet.
        ApplyAutoExpansion();
    }

    /// <summary>
    /// Freezes only the presentation of in-flight work after a user asks the agent to stop.
    /// The run and correlation state stay alive: a later real tool_result can still overwrite
    /// these optimistic values while the host finishes the current iteration.
    /// </summary>
    public void CompleteOptimistically()
    {
        if (Activity.IsTerminal) return;

        var completedAt = DateTimeOffset.UtcNow;
        foreach (var running in Activity.Steps.Where(
                     step => step.Status is ToolStepStatus.Running or ToolStepStatus.Pending))
        {
            running.Status = ToolStepStatus.Success;
            running.CompletedAt = completedAt;
            running.DurationMs ??= Math.Max(
                0, (completedAt - running.StartedAt).TotalMilliseconds);
        }

        Activity.Status = ToolActivityStatus.Success;
        Activity.CompletedAt = completedAt;
        var count = Activity.Steps.Count;
        Activity.Summary = $"Completed · {count} steps · {Activity.DurationLabel}";
        Activity.RefreshPresentation();
    }

    /// <summary>Seals the activity when the turn ends. Steps left running never get a result
    /// frame (the turn is over), so they are closed out here rather than spinning forever.</summary>
    public void Complete(ToolActivityStatus? terminalStatus = null, string? error = null)
    {
        // A finished turn whose embedded approval is still pending means the decision was made
        // outside this card — the agent proceeded (an approval satisfied elsewhere, e.g. a separate
        // ask_user), or the turn was stopped. Either way the wait is over, so drop the stale
        // approval; keeping it would leave a completed card advertising "Waiting for approval". A
        // genuinely answered approval (resolved phase) is left in place so its result line stays.
        if (Activity.Approval is { IsApprovalPending: true })
            Activity.Approval = null;

        foreach (var running in Activity.Steps.Where(step => step.Status is ToolStepStatus.Running or ToolStepStatus.Pending))
        {
            // Warning, not Failed: the tool may well have succeeded — all we actually know is
            // that we never saw its result, which is a reporting gap, not a proven failure.
            running.Status = terminalStatus == ToolActivityStatus.Cancelled ? ToolStepStatus.Cancelled : ToolStepStatus.Warning;
            running.Summary ??= terminalStatus == ToolActivityStatus.Cancelled ? "Cancelled" : "No result returned";
            running.CompletedAt = DateTimeOffset.UtcNow;
            running.DurationMs ??= Math.Max(0, (running.CompletedAt.Value - running.StartedAt).TotalMilliseconds);
        }

        // A rolled-up status is only ever PartialSuccess or Success: one bad step among many
        // does not make the activity a failure, and the caller can still pass Failed/Cancelled
        // explicitly when the *turn* actually died.
        var hasFailed = Activity.Steps.Any(step => step.Status == ToolStepStatus.Failed);
        var hasWarning = Activity.Steps.Any(step => step.Status == ToolStepStatus.Warning);
        Activity.Status = terminalStatus ?? (hasFailed ? ToolActivityStatus.PartialSuccess : hasWarning ? ToolActivityStatus.PartialSuccess : ToolActivityStatus.Success);
        AnchorSpanOnSteps();
        Activity.ErrorSummary = error is null ? Activity.ErrorSummary : FriendlyProblem(error);
        var count = Activity.Steps.Count;
        // The header sits beside the literal "Tool activity" title and above the timeline's own
        // "Done"/"Failed" completion row, so the old "Tool execution completed …" phrasing said the
        // subject three times over. The status word alone ("Completed", "Failed") carries it — the
        // title supplies "Tool", the row supplies the finality — leaving the summary to do the one
        // thing only it can: the counts and the elapsed span.
        Activity.Summary = Activity.Status switch
        {
            ToolActivityStatus.Failed => $"Failed · {Activity.ErrorSummary ?? "view details"}",
            ToolActivityStatus.Cancelled => $"Cancelled · {count} steps",
            ToolActivityStatus.PartialSuccess => $"Completed · {Activity.Steps.Count(step => step.Status is ToolStepStatus.Warning or ToolStepStatus.Failed)} steps had issues",
            _ => $"Completed · {count} steps · {Activity.DurationLabel}",
        };
        ApplyAutoExpansion();

        Activity.RefreshPresentation();
    }

    /// <summary>
    /// Re-anchors the card's <see cref="ToolActivityViewModel.StartedAt"/>/<c>CompletedAt</c> on
    /// the steps' own timestamps, which is what makes the header's duration mean anything.
    ///
    /// <para><b>The activity cannot time itself.</b> <c>StartedAt</c> is stamped in the view
    /// model's constructor, so it measures how long the <i>projection object</i> has existed — and
    /// the headless persist path builds a fresh one at save time, replays the whole turn into it
    /// in a tight loop, and completes it a millisecond later. Every reopened conversation
    /// therefore reported its tool work as "· 2 ms" no matter how long it really took.</para>
    ///
    /// <para>The steps carry the real times: each one is stamped from its frame's own <c>ts</c>
    /// (falling back to the clock when a host omits it), so the span from the first call to the
    /// last result survives being replayed hours later. It also makes the live card and the
    /// persisted one agree, which the wall-clock version never did.</para>
    ///
    /// <para>With no steps there is nothing to measure and the clock is the only answer — that
    /// card shows no duration in its summary anyway.</para>
    /// </summary>
    private void AnchorSpanOnSteps()
    {
        if (Activity.Steps.Count == 0)
        {
            Activity.CompletedAt = DateTimeOffset.UtcNow;
            return;
        }

        var first = Activity.Steps.Min(step => step.StartedAt);
        var last = Activity.Steps.Max(step => step.CompletedAt ?? step.StartedAt);

        Activity.StartedAt = first;
        // Never let the span run backwards: a host whose clock disagrees with ours, or a step
        // stamped from `ts` beside one that fell back to the local clock, can order these two
        // either way — and DurationSeconds floors at zero, so an inverted pair would silently
        // read as "0 ms" rather than as the bad data it is.
        Activity.CompletedAt = last < first ? first : last;
    }

    /// <summary>Human label for a tool. The table covers the tools the reference agents ship
    /// with; anything else falls back to title-casing the raw name, so a custom tool still
    /// reads as words rather than an identifier.
    ///
    /// <para>These are <i>verbs</i>, not descriptions, because the row now prints the tool's
    /// actual argument beneath them (see <see cref="ToolInvocations"/>) — "Click" over "the Sign in
    /// button" reads as a sentence, where the older "Click element" left the row saying what kind
    /// of thing it clicked but never which one. The browser block mirrors the reference web
    /// client's <c>describeAction</c> table so the same run reads the same way in both clients.</para></summary>
    public static string DisplayName(string toolName) => toolName.ToLowerInvariant() switch
    {
        // ---- browser: navigation ----
        "open_browser" => "Open browser",
        "go_to" or "navigate" => "Open page",
        "newtab" => "New tab",
        "close_tab" => "Close tab",
        "get_current_url" => "Get URL",
        // ---- browser: pointer ----
        "click" or "click_element_by_selector" or "click_element_near_selector" => "Click",
        "double_click" => "Double-click",
        "right_click" => "Right-click",
        "hover" => "Hover",
        "mouse_click" => "Click at point",
        "scroll" => "Scroll",
        "wait" => "Wait",
        "set_viewport" => "Set viewport",
        // ---- browser: reading ----
        "get_text" => "Read page text",
        "extract_data" => "Extract data",
        "extract_items_by_selector" => "Extract items",
        "get_links_from_page" => "Extract links",
        "get_element_text_by_selector" => "Read element",
        "count_elements_by_selector" => "Count elements",
        "find_element_by_description" => "Find element",
        // ---- browser: input ----
        "type_text" or "type" or "type_text_by_selector" or "keyboard_type" => "Type text",
        "keyboard_press" => "Press key",
        "select_option" => "Select option",
        "check_checkbox" => "Check box",
        "upload_file_by_selector" or "upload_file_after_click_by_selector" => "Upload file",
        // ---- browser: capture / scripting ----
        "take_screenshot" => "Take screenshot",
        "save_page_context" => "Save page context",
        "save_state" => "Save login state",
        "run_page_script" or "run_frame_script" => "Run script",
        // ---- shell, files, search ----
        "search_web" or "web_search" or "search" => "Search web",
        "grep" => "Search files",
        "glob" => "Match files",
        "find" => "Find files",
        "read" or "read_file" => "Read file",
        "write" or "write_file" => "Write file",
        "edit" => "Edit file",
        "send_email" => "Send email",
        "delete_file" => "Delete file",
        "bash" or "shell" or "run" or "run_command" or "execute_command" => "Run command",
        "run_background" => "Run in background",
        "upload_file" => "Upload file",
        "download_file" => "Download file",
        // ---- delegated work ----
        "call_omo_agent" => "Delegate to agent",
        "background_output" => "Read background output",
        "background_cancel" => "Cancel background task",
        _ => ToReadableName(toolName),
    };

    public static bool IsHighRisk(string toolName)
    {
        var normalized = toolName.ToLowerInvariant();
        return HighRiskTokens.Any(token => normalized.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>Redacts a JSON argument/result blob key-by-key. Falls back to the text scrubber
    /// when the payload does not parse — a tool that returns something other than JSON must
    /// still not be able to print a bearer token into the timeline.</summary>
    public static string SanitizeJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, DisplayJsonOptions))
            {
                WriteRedacted(writer, doc.RootElement);
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch { return SanitizeText(raw); }
    }

    /// <summary>
    /// Writer settings for arguments that will be <i>read by a person</i>.
    ///
    /// <para><b>The relaxed encoder is the point.</b> The default encoder escapes
    /// far more than JSON requires — not only non-ASCII, but <c>+</c>, <c>&amp;</c>, <c>&lt;</c> and
    /// <c>&gt;</c> — so a perfectly ordinary search URL reached the timeline as
    /// <c>google.com/search?q=Sydney+weather&ia=web</c>, and any Chinese argument arrived
    /// as a wall of <c>\uXXXX</c>. Nothing was wrong with the data; it was unreadable purely because
    /// of how it was re-encoded for display.</para>
    ///
    /// <para><b>"Unsafe" names an HTML/JS-injection concern that does not exist on this path.</b>
    /// The result is rendered into a XAML <c>TextBlock</c> (which interprets no markup), written
    /// through a parameterized SQLite command, and re-parsed as JSON — never interpolated into a
    /// page or a script. Do not copy this encoder to somewhere that <i>is</i> such a context.</para>
    /// </summary>
    private static readonly JsonWriterOptions DisplayJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>How much of a tool result the card keeps in memory. Generous next to what the
    /// scroller shows, small next to what a scrape returns.</summary>
    private const int MaxRetainedResultChars = 8 * 1024;

    private static string Cap(string value)
        => value.Length <= MaxRetainedResultChars
            ? value
            : value[..MaxRetainedResultChars] + "\n… (truncated)";

    /// <summary>Scrubs <c>key: value</c> / <c>key=value</c> secrets out of free-form text (log
    /// lines, stack traces, HTML). Best-effort by construction — it only catches the labelled
    /// form, so a bare token in prose survives; treat it as reducing accidental exposure in the
    /// UI, not as a guarantee.
    ///
    /// <para>Then a second, exact pass over anything the user typed into a credentials form this
    /// run (<see cref="SessionSecrets"/>). The labelled-form regex above cannot help there: an
    /// agent typing a password into a browser sends it as <c>{"text": "hunter2"}</c>, where nothing
    /// names it a secret and only knowing the value gives it away.</para></summary>
    public static string SanitizeText(string value)
    {
        var scrubbed = LabelledSecrets.Replace(value ?? "", "$1=[hidden]");
        // Skipped entirely when no credential has ever been entered, which is the usual case.
        return SessionSecrets.IsEmpty ? scrubbed : SessionSecrets.Redact(scrubbed);
    }

    /// <summary>Rebuilds a JSON tree with sensitive values replaced and every string scrubbed.
    /// Reconstructing rather than string-editing is what makes the redaction total: a secret
    /// nested ten levels down in an array is reached by the same recursion as a top-level one.
    /// Unhandled kinds (null, undefined, comments) collapse to null, which is lossless enough
    /// for a display-only payload.
    ///
    /// <para>Writes straight into the <see cref="Utf8JsonWriter"/> rather than building an
    /// intermediate <c>object?</c> tree for <c>JsonSerializer.Serialize</c> to walk. That
    /// serializer is reflection-based, which under <c>PublishTrimmed=true</c> throws instead of
    /// running (see <c>docs/TRIMMING.md</c>) — and this path is on every persisted tool step, so
    /// it took the whole Tool Activity timeline down with it. Emitting the document directly is
    /// trim-safe by construction and skips the boxing besides.</para></summary>
    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (SensitiveKeys.Contains(property.Name)) writer.WriteStringValue("[hidden]");
                    else WriteRedacted(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteRedacted(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(SanitizeText(element.GetString() ?? ""));
                break;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer)) writer.WriteNumberValue(integer);
                else writer.WriteNumberValue(element.GetDouble());
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>The id that binds a result back to its call. Agents disagree on the property
    /// name, so all the spellings in the wild are probed in order; the stream event's own id is
    /// the last resort, and it only correlates when the host echoes it on both frames.</summary>
    private static string? CorrelationId(JsonElement root, AgentStreamEvent e)
        => String(root, "tool_id") ?? String(root, "toolCallId") ?? String(root, "call_id") ?? String(root, "callId") ?? String(root, "invocation_id") ?? String(root, "invocationId") ?? String(root, "id") ?? e.EventId;
    // Kind-checked accessors: every read on this path is against an agent-supplied payload of
    // no guaranteed shape, so a wrong type must return null rather than throw. Nothing here
    // coerces (a number in a string slot reads as absent) — that keeps a malformed frame
    // rendering as a step with a missing field instead of a plausible-looking wrong one.
    private static string? String(JsonElement root, string property) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Number(JsonElement root, string property) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    private static string? ObjectJson(JsonElement root, string property) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? value.GetRawText() : null;

    /// <summary>Reads the frame's <c>ts</c>. Agents send either seconds or milliseconds with no
    /// marker, so the magnitude decides: 1e12 is year 33658 in seconds and 2001 in
    /// milliseconds, which makes it an unambiguous split for any timestamp this app will see.
    ///
    /// <para>Both branches go through <c>FromUnixTimeMilliseconds</c> because a seconds-valued
    /// <c>ts</c> is a <i>fractional</i> double — the host sends 1784467095.5 — and casting that
    /// straight to long threw the fraction away, quantising every derived duration to a whole
    /// second. Scaling to milliseconds first keeps it, which is the difference between a card
    /// reading "7.2 s" and "7.0 s".</para></summary>
    private static DateTimeOffset? Timestamp(JsonElement root)
    {
        var ts = Number(root, "ts");
        if (ts is null) return null;
        // Out-of-range values throw rather than clamping; a nonsense timestamp falls back to
        // the local clock at the call site instead of taking the turn down.
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(
                (long)(ts > 1_000_000_000_000 ? ts.Value : ts.Value * 1000));
        }
        catch { return null; }
    }

    /// <summary>The "what did it act on" subtitle beside a step: a host, a filename, a search
    /// phrase. Probed in specificity order — a URL's host identifies the step better than the
    /// path it happens to also carry — and an empty string is a fine answer, since the row
    /// still reads correctly with just the tool's display name.
    /// <para><c>toolName</c> is unused today; the parameter is kept so a per-tool rule can be
    /// added here without touching the call sites.</para></summary>
    private static string DisplayTarget(string toolName, string args)
    {
        // Arguments are agent-supplied and not guaranteed to be JSON at all; anything that
        // fails to parse simply has no target.
        try
        {
            using var doc = JsonDocument.Parse(args);
            var root = doc.RootElement;
            foreach (var name in UrlPropertyNames)
            {
                if (String(root, name) is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri.Host;
            }
            if (String(root, "query") is { Length: > 0 } query) return $"Search: {Truncate(query, 48)}";
            foreach (var name in PathPropertyNames)
            {
                if (String(root, name) is { Length: > 0 } path) return path.Replace('\\', '/').Split('/').Last();
            }
            if (String(root, "text") is { Length: > 0 } text) return Truncate(text, 48);
        }
        catch { }
        return "";
    }

    /// <summary>Decides how a completed step reads. The host's own <c>status</c> field is
    /// authoritative for failure; everything else is inferred from the result text, because
    /// most tools report their problems in prose and never set a status at all. Order matters:
    /// failure beats warning, and an empty result is a warning rather than a success.
    ///
    /// <para><b>The prose heuristics are skipped entirely for a tool that returns content</b>
    /// (<see cref="ToolInvocations.ReturnsContent"/>). Scanning a result for words like "failed"
    /// only makes sense when that result is the tool talking about itself; when it is a file, a
    /// document or a set of search hits, those words are the content's, and matching them marked
    /// perfectly successful reads red — a system prompt describing error handling, a log file, any
    /// source file containing "exception". Such a step can still fail, but only the host may say
    /// so, via <paramref name="transportStatus"/>.</para></summary>
    private static (ToolStepStatus Status, string Summary) Classify(
        string? transportStatus,
        string result,
        string? toolName)
    {
        var text = result ?? "";

        // Always authoritative, for every kind of tool.
        if (string.Equals(transportStatus, "error", StringComparison.OrdinalIgnoreCase))
            return (ToolStepStatus.Failed, FriendlyProblem(text));

        if (ToolInvocations.ReturnsContent(toolName))
        {
            return string.IsNullOrWhiteSpace(text)
                ? (ToolStepStatus.Warning, "No content returned")
                : (ToolStepStatus.Success, ResultSummary(text));
        }

        if (FailurePatterns.Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            return (ToolStepStatus.Failed, FriendlyProblem(text));
        if (string.IsNullOrWhiteSpace(text)
            || WarningPatterns.Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            || HttpProblemCodes.IsMatch(text))
            return (ToolStepStatus.Warning, string.IsNullOrWhiteSpace(text) ? "No content returned" : FriendlyProblem(text));
        return (ToolStepStatus.Success, ResultSummary(text));
    }

    private static string RunningSummary(ToolStepViewModel step)
    {
        var completed = step.Sequence - 1;
        return completed > 0 ? $"{completed} steps completed, {step.DisplayName}…" : $"{step.DisplayName}…";
    }
    /// <summary>Turns a raw failure string into one short phrase a non-developer can act on.
    /// The checks are ordered most-specific-first (403 before generic permission wording), and
    /// anything unrecognised falls through to a scrubbed, single-line, truncated excerpt —
    /// showing a strange error badly is still better than hiding it.</summary>
    private static string FriendlyProblem(string value)
    {
        // "403" is matched with boundaries here for the same reason it is in HttpProblemCodes —
        // a bare Contains hits hex ids and query strings.
        if (value.Contains("access denied", StringComparison.OrdinalIgnoreCase) || value.Contains("forbidden", StringComparison.OrdinalIgnoreCase) || ForbiddenCode.IsMatch(value)) return "Page access denied";
        if (value.Contains("permission denied", StringComparison.OrdinalIgnoreCase) || value.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)) return "Permission denied";
        if (value.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return "Request timed out";
        if (value.Contains("connection refused", StringComparison.OrdinalIgnoreCase)) return "Connection refused";
        if (value.Contains("captcha", StringComparison.OrdinalIgnoreCase)) return "CAPTCHA required";
        if (value.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) return "Rate limited";
        if (value.Contains("no content", StringComparison.OrdinalIgnoreCase) || value.Contains("empty", StringComparison.OrdinalIgnoreCase)) return "No content found";
        return Truncate(SanitizeText(value).Replace('\r', ' ').Replace('\n', ' '), 72);
    }
    private static string ResultSummary(string value) => Truncate(SanitizeText(value).Replace('\r', ' ').Replace('\n', ' '), 72);
    private static string ToReadableName(string value) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.Replace('_', ' ').Replace('-', ' '));
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
}
