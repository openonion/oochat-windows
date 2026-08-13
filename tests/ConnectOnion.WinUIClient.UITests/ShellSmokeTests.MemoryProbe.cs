using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;

namespace ConnectOnion.WinUIClient.UITests;

/// <summary>
/// Diagnostic probe (opt-in, never in CI): drives many real turns through one conversation against
/// the loopback fake agent and samples the app process's private bytes after each one. This is the
/// shape the existing MemoryLeakTests do <b>not</b> cover — they open and close surfaces, whereas
/// this holds one page open and lets a transcript accumulate, which is what a user chatting sees.
/// </summary>
public sealed partial class ShellSmokeTests
{
    private const string MemoryProbeVariable = "CONNECTONION_MEMORY_PROBE";
    private const string MemoryProbeReportVariable = "CONNECTONION_MEMORY_PROBE_REPORT";

    /// <summary>A reply that exercises the renderer's expensive paths — table, fenced code,
    /// nested lists, inline code, a rule — rather than a run of plain characters.</summary>
    private const string RichReply = """
        ## Result

        | Check | Status | Detail |
        |---|:--:|---|
        | config parse | ok | `settings.yaml` |
        | retries | ok | raised to `3` |
        | timeout | changed | 30s -> 60s |

        - first finding with `inline code` and **bold**
        - second finding
          - nested detail line
        - [x] applied
        - [ ] pending review

        ```csharp
        public void Apply(Settings s) => s.Timeout = TimeSpan.FromSeconds(60);
        ```

        ---

        Done.
        """;

    [Fact]
    public async Task Chat_ManyTurnsInOneConversation_MemoryProbe()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(MemoryProbeVariable), "1", StringComparison.Ordinal))
            return;

        var turns = ReadInt("CONNECTONION_MEMORY_PROBE_TURNS", 40);
        var thoughtsPerTurn = ReadInt("CONNECTONION_MEMORY_PROBE_THOUGHTS", 6);
        var thoughtChars = ReadInt("CONNECTONION_MEMORY_PROBE_THOUGHT_CHARS", 400);
        var replyChars = ReadInt("CONNECTONION_MEMORY_PROBE_REPLY_CHARS", 1200);

        var minimal = string.Equals(
            Environment.GetEnvironmentVariable("CONNECTONION_MEMORY_PROBE_MINIMAL"), "1", StringComparison.Ordinal);
        // "Rich" adds the card kinds a prose-only turn never builds: the tool timeline
        // (ToolActivityView, its per-step ToolResultTextBlock and markdown log) and the diff card.
        var rich = string.Equals(
            Environment.GetEnvironmentVariable("CONNECTONION_MEMORY_PROBE_RICH"), "1", StringComparison.Ordinal);
        var toolsPerTurn = ReadInt("CONNECTONION_MEMORY_PROBE_TOOLS", 4);
        var toolResult = string.Join('\n',
            Enumerable.Range(0, 12).Select(i => $"src/module{i}/handler.cs:{i * 7 + 3}: // TODO tidy this up"));
        var thought = new string('t', thoughtChars);
        var reply = new string('r', replyChars);

        var holdSocket = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnsServed = 0;

        await using var server = new UiFakeAgentServer(async (_, socket, ct) =>
        {
            await UiFakeAgentServer.ReceiveFrameOfTypeAsync(socket, "CONNECT", ct);
            await UiFakeAgentServer.SendTextAsync(socket,
                "{\"type\":\"CONNECTED\",\"session_id\":\"ui-remote\",\"status\":\"connected\"}", ct);

            while (!ct.IsCancellationRequested)
            {
                await UiFakeAgentServer.ReceiveFrameOfTypeAsync(socket, "INPUT", ct);
                var turn = Interlocked.Increment(ref turnsServed);

                if (!minimal)
                {
                    await UiFakeAgentServer.SendTextAsync(socket,
                        $$"""{"type":"llm_call","id":"llm-{{turn}}","model":"probe-model"}""", ct);

                    for (var step = 0; step < thoughtsPerTurn; step++)
                    {
                        // Paced like a real stream so the UI actually renders each event rather
                        // than collapsing the whole turn into one dispatcher drain.
                        await Task.Delay(40, ct);
                        await UiFakeAgentServer.SendTextAsync(socket,
                            $$"""{"type":"thinking","id":"th-{{turn}}-{{step}}","content":"{{thought}}"}""", ct);
                    }

                    await UiFakeAgentServer.SendTextAsync(socket,
                        $$"""
                        {"type":"llm_result","id":"llmr-{{turn}}","model":"probe-model","tokens_input":1200,"tokens_output":340,"duration_ms":900,"context_percent":12.5,"tool_calls_count":0}
                        """, ct);

                    if (rich)
                    {
                        // Every card kind a turn can produce, so the probe covers the tool
                        // timeline and the diff card rather than prose bubbles alone. Interactive
                        // cards (ask_user/approval_needed/plan_review) are deliberately absent:
                        // they park the turn on a human, which a throughput loop cannot answer.
                        for (var call = 0; call < toolsPerTurn; call++)
                        {
                            await Task.Delay(30, ct);
                            await UiFakeAgentServer.SendTextAsync(socket,
                                JsonSerializer.Serialize(new Dictionary<string, object?>
                                {
                                    ["type"] = "tool_call",
                                    ["id"] = $"tc-{turn}-{call}",
                                    ["tool"] = call % 2 == 0 ? "bash" : "read_file",
                                    ["arguments"] = call % 2 == 0
                                        ? """{"command":"grep -rn TODO src/ | head -40"}"""
                                        : """{"path":"/srv/app/config/settings.yaml"}""",
                                }), ct);
                            await Task.Delay(30, ct);
                            await UiFakeAgentServer.SendTextAsync(socket,
                                JsonSerializer.Serialize(new Dictionary<string, object?>
                                {
                                    ["type"] = "tool_result",
                                    ["id"] = $"tc-{turn}-{call}",
                                    ["tool"] = call % 2 == 0 ? "bash" : "read_file",
                                    ["status"] = "success",
                                    ["result"] = toolResult,
                                }), ct);
                        }

                        await UiFakeAgentServer.SendTextAsync(socket,
                            JsonSerializer.Serialize(new Dictionary<string, object?>
                            {
                                ["type"] = "diff_preview",
                                ["id"] = $"diff-{turn}",
                                ["path"] = "/srv/app/config/settings.yaml",
                                ["diff"] = "@@ -1,4 +1,4 @@\n-timeout: 30\n+timeout: 60\n retries: 3\n",
                            }), ct);
                    }

                    await UiFakeAgentServer.SendTextAsync(socket,
                        JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["type"] = "assistant",
                            ["id"] = $"as-{turn}",
                            ["content"] = rich ? $"Turn {turn}\n\n{RichReply}" : $"Turn {turn} {reply}",
                        }), ct);
                }

                await UiFakeAgentServer.SendTextAsync(socket,
                    $$"""{"type":"OUTPUT","result":"Turn {{turn}} {{reply}}","duration_ms":950}""", ct);
            }

            await holdSocket.Task.WaitAsync(ct);
        });

        if (!PrepareChatProfile(server.BaseUri)) return;

        // Invariant throughout: this report is parsed, pasted into issues and diffed against
        // earlier runs, so a decimal comma on one machine would make two runs incomparable.
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"turns={turns} thoughts/turn={thoughtsPerTurn} thoughtChars={thoughtChars} replyChars={replyChars}");
        report.AppendLine("turn\tprivateMB\tworkingSetMB\thandles\tthreads");

        try
        {
            using var launched = LaunchApp(handleFirstRunDialog: false);
            if (launched is null) return;

            var window = launched.Window;
            var input = OpenChat(window);
            var send = WaitForDescendant(window, "SendMessageButton");
            Assert.NotNull(send);

            var process = launched.Process;
            Sample(report, process, 0);

            for (var turn = 1; turn <= turns; turn++)
            {
                input.Text = $"probe turn {turn}";
                Assert.True(WaitUntil(() => send.IsEnabled), $"Send stayed disabled before turn {turn}.");
                send.AsButton().Invoke();

                var expected = turn;
                Assert.True(
                    WaitUntilLong(() => Volatile.Read(ref turnsServed) >= expected, TimeSpan.FromSeconds(30)),
                    $"The fake agent never received turn {turn}.");
                // The Stop action exists only while a run is live, so its disappearance is the
                // composer's own "this turn reached a terminal state" signal.
                Assert.True(
                    WaitUntilLong(
                        () => window.FindFirstDescendant(
                            query => query.ByAutomationId("StopResponseButton")) is null,
                        TimeSpan.FromSeconds(30)),
                    $"Turn {turn} never settled.");

                Thread.Sleep(250);
                Sample(report, process, turn);
            }

            server.ThrowIfFaulted();
        }
        finally
        {
            holdSocket.TrySetResult();
            var path = Environment.GetEnvironmentVariable(MemoryProbeReportVariable);
            if (!string.IsNullOrWhiteSpace(path)) File.WriteAllText(path, report.ToString());
            RemoveChatFixture();
        }
    }

    private static void Sample(StringBuilder report, Process process, int turn)
    {
        process.Refresh();
        report.AppendLine(string.Join('\t',
            turn,
            (process.PrivateMemorySize64 / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture),
            (process.WorkingSet64 / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture),
            process.HandleCount,
            // Threads are the tripwire that caught the Win2D leak: a per-turn slope here is a
            // retained native resource, whichever way the private-byte curve is read.
            process.Threads.Count));
    }

    private static bool WaitUntilLong(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition()) return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                           or System.Runtime.InteropServices.COMException)
            {
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static int ReadInt(string variable, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(variable), out var value) && value > 0
            ? value
            : fallback;
}
