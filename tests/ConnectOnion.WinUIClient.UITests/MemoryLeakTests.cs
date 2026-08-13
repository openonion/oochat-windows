using System.Diagnostics;
using System.Runtime.InteropServices;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace ConnectOnion.WinUIClient.UITests;

/// <summary>
/// Real-process leak regression for the app's expensive navigation surfaces. This is an opt-in
/// desktop test: run it through scripts/Test-MemoryLeaks.ps1 so the executable, isolated data
/// root, cycle count, and thresholds are configured consistently.
/// </summary>
public sealed class MemoryLeakTests(ITestOutputHelper output)
{
    private const string EnabledVariable = "CONNECTONION_MEMORY_TEST";
    private const string RequireConversationVariable = "CONNECTONION_MEMORY_REQUIRE_CONVERSATION";
    private const string SnapshotDirectoryVariable = "CONNECTONION_MEMORY_SNAPSHOT_DIR";
    private const string GcDumpToolVariable = "CONNECTONION_GCDUMP_TOOL";
    private const string HeapDumpToolVariable = "CONNECTONION_DUMP_TOOL";

    [Fact]
    public async Task NavigationSurfaces_RepeatedOpenClose_ReachesStableHighWaterMark()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal))
            return;

        // WinUI and the CLR release large transcript trees in batches. A shorter 16/4/500 window
        // can end just before that batch and report a false linear slope, even though the next
        // few samples drop. Keep the strict thresholds; make the observation window long enough
        // to include at least one normal reclamation cycle.
        var cycles = ReadPositiveInt("CONNECTONION_MEMORY_CYCLES", 24);
        var warmupCycles = ReadPositiveInt("CONNECTONION_MEMORY_WARMUP_CYCLES", 6);
        var settleMs = ReadPositiveInt("CONNECTONION_MEMORY_SETTLE_MS", 750);
        var scenarios = ReadScenarios();
        var baselineDataRoot = Environment.GetEnvironmentVariable("CONNECTONION_DATA_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(baselineDataRoot),
            "CONNECTONION_DATA_ROOT must point at the isolated baseline profile.");

        if (scenarios.Contains("conversation-alternating"))
        {
            await EnsureConversationCountAsync(4);
        }

        // Each surface gets a fresh process. Otherwise the JIT/XAML/font high-water marks from
        // earlier surfaces are charged to the next one and can look like that surface's slope.
        // The isolated data root is shared, but only one app owns it at a time.
        var ordered = new[]
            { "settings", "add-agent", "agent-detail", "conversation", "conversation-alternating" };
        foreach (var scenario in ordered.Where(scenarios.Contains))
        {
            var scenarioRoot = Path.Combine(
                Path.GetTempPath(), $"ConnectOnion-memory-scenario-{Guid.NewGuid():N}");
            try
            {
                CopyDirectory(baselineDataRoot!, scenarioRoot);
                Environment.SetEnvironmentVariable("CONNECTONION_DATA_ROOT", scenarioRoot);
                RunScenarioInFreshProcess(scenario, cycles, warmupCycles, settleMs);
            }
            finally
            {
                Environment.SetEnvironmentVariable("CONNECTONION_DATA_ROOT", baselineDataRoot);
                if (Directory.Exists(scenarioRoot)) Directory.Delete(scenarioRoot, recursive: true);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    /// <summary>
    /// Expands only the already-isolated memory-test profile so the alternating scenario really
    /// fills the four-entry conversation LRU. Cloning an existing transcript gives each page a
    /// representative XAML tree; the user's source profile remains untouched by the wrapper.
    /// </summary>
    private static async Task EnsureConversationCountAsync(int minimum)
    {
        var sessions = new SessionRepository();
        var state = await sessions.LoadAsync();
        if (state.Sessions.Count == 0) return;

        var conversations = new ConversationRepository();
        var sourceSessions = state.Sessions.ToArray();
        var source = sourceSessions.FirstOrDefault(session => session.Id == state.ActiveSessionId)
            ?? sourceSessions[0];
        var existingForAgent = state.Sessions.Count(session => session.AgentId == source.AgentId);
        if (existingForAgent >= minimum) return;

        var sourceMessages = new Dictionary<string, IReadOnlyList<ChatMessage>>();
        sourceMessages[source.Id] = await conversations.LoadMessagesAsync(source.Id);

        var clones = new List<(SessionSummary Session, IReadOnlyList<ChatMessage> Messages)>();
        for (var index = existingForAgent; index < minimum; index++)
        {
            var timestamp = DateTime.UtcNow.AddMilliseconds(index).ToString("o");
            var clone = new SessionSummary
            {
                Id = Guid.NewGuid().ToString(),
                AgentId = source.AgentId,
                Title = $"Memory test conversation {index + 1}",
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                Mode = source.Mode,
            };
            state.Sessions.Add(clone);
            clones.Add((clone, sourceMessages[source.Id]));
        }

        await sessions.SaveAsync(state);
        foreach (var (session, messages) in clones)
        {
            await conversations.UpsertMessagesAsync(session.Id, messages);
        }
    }

    private void RunScenarioInFreshProcess(string scenario, int cycles, int warmupCycles, int settleMs)
    {
        using var launched = ShellSmokeTests.LaunchApp(handleFirstRunDialog: false);
        Assert.NotNull(launched);
        var window = launched.Window;

        if (scenario == "conversation-alternating" && FindConversationButtons(window).Length < 2)
        {
            output.WriteLine("conversation-alternating skipped: fewer than two visible conversations");
            return;
        }

        Action action = scenario switch
        {
            "settings" => () =>
            {
                ShellSmokeTests.OpenSettings(window);
                InvokeByAutomationId(window, "SettingsCloseButton");
            }
            ,
            "add-agent" => () =>
            {
                InvokeByAutomationId(window, "AddAgentButton");
                InvokeByName(window, "Cancel adding agent");
            }
            ,
            "agent-detail" => () =>
            {
                var button = FindAgentButton(window);
                Assert.NotNull(button);
                Invoke(button);
                ReturnToHome(window);
            }
            ,
            "conversation" => () =>
            {
                var button = FindConversationButton(window);
                Assert.NotNull(button);
                Invoke(button);
                ReturnToHome(window);
            }
            ,
            "conversation-alternating" => AlternatingConversationAction(window),
            _ => throw new InvalidOperationException($"Unknown memory scenario: {scenario}"),
        };

        if (scenario == "agent-detail" && FindAgentButton(window) is null)
            Assert.Fail("No agent button was available for the agent-detail memory scenario.");

        if (scenario.StartsWith("conversation", StringComparison.Ordinal)
            && FindConversationButton(window) is null)
        {
            var required = !string.Equals(
                Environment.GetEnvironmentVariable(RequireConversationVariable), "0", StringComparison.Ordinal);
            Assert.False(required,
                "No conversation button was available. Use a profile containing a conversation, " +
                "or set CONNECTONION_MEMORY_REQUIRE_CONVERSATION=0 for shell-only coverage.");
            output.WriteLine($"{scenario} skipped: the isolated profile contains no conversation");
            return;
        }

        RunScenario(launched, scenario, cycles, warmupCycles, settleMs, action);
    }

    private Action AlternatingConversationAction(Window window)
    {
        var index = 0;
        var sessionCount = Math.Min(4, FindConversationButtons(window).Length);
        output.WriteLine($"conversation-alternating cycles through {sessionCount} sessions");
        return () =>
        {
            var buttons = FindConversationButtons(window);
            Assert.True(buttons.Length >= sessionCount,
                $"{sessionCount} conversations were available at warmup but not after navigation.");
            Invoke(buttons[index++ % sessionCount]);
            ReturnToHome(window);
        };
    }

    private static void ReturnToHome(Window window)
    {
        // The old memory harness searched for the removed accessible name "ConnectOnion home".
        // The fixed Agent library navigation row is the product's current route back to Home and
        // its AutomationId is locale-independent, so the diagnostic remains valid in zh-CN too.
        InvokeByAutomationId(window, "AgentsNavigationButton");
        var home = FindFirst(window, query => query.ByAutomationId("HomeAddAgentButton"), attempts: 100);
        Assert.NotNull(home);
    }

    private void RunScenario(
        ShellSmokeTests.LaunchedApp launched,
        string name,
        int cycles,
        int warmupCycles,
        int settleMs,
        Action action)
    {
        for (var index = 0; index < warmupCycles; index++)
        {
            action();
            Thread.Sleep(settleMs);
        }

        CaptureManagedSnapshot(launched.Process, name, "00-baseline");

        var samples = new List<MemorySample>(cycles);
        for (var index = 0; index < cycles; index++)
        {
            action();
            Thread.Sleep(settleMs);
            var sample = MemorySample.Capture(launched.Process, index);
            samples.Add(sample);
            output.WriteLine($"{name}[{index:D2}] {sample}");

            if (index == 24) CaptureManagedSnapshot(launched.Process, name, "25-cycles");
            if (index == 49)
            {
                CaptureManagedSnapshot(launched.Process, name, "50-cycles");
                CaptureHeapDump(launched.Process, name, "50-cycles");
            }
        }

        AssertPlateau(name, samples);
    }

    private void AssertPlateau(string scenario, IReadOnlyList<MemorySample> samples)
    {
        Assert.True(samples.Count >= 8, "Memory leak tests require at least 8 measured cycles.");
        var tail = samples.Skip(samples.Count / 2).ToArray();

        var maxPrivateSlope = ReadPositiveDouble("CONNECTONION_MEMORY_MAX_PRIVATE_SLOPE_MB", 1.5);
        var maxPrivateSpan = ReadPositiveDouble("CONNECTONION_MEMORY_MAX_PRIVATE_TAIL_SPAN_MB", 24.0);
        var maxHandleSpan = ReadPositiveInt("CONNECTONION_MEMORY_MAX_HANDLE_TAIL_SPAN", 64);
        var maxThreadSpan = ReadPositiveInt("CONNECTONION_MEMORY_MAX_THREAD_TAIL_SPAN", 8);

        var privateSlope = LinearSlope(tail.Select(sample => sample.PrivateMb).ToArray());
        var privateSpan = tail.Max(sample => sample.PrivateMb) - tail.Min(sample => sample.PrivateMb);
        var handleSpan = tail.Max(sample => sample.Handles) - tail.Min(sample => sample.Handles);
        var threadSpan = tail.Max(sample => sample.Threads) - tail.Min(sample => sample.Threads);

        var summary =
            $"{scenario}: tail private slope={privateSlope:F2} MB/cycle, " +
            $"private span={privateSpan:F1} MB, handle span={handleSpan}, thread span={threadSpan}";
        output.WriteLine(summary);

        Assert.True(privateSlope <= maxPrivateSlope,
            $"Possible unbounded native/managed retention. {summary}; limit={maxPrivateSlope:F2} MB/cycle.");
        Assert.True(privateSpan <= maxPrivateSpan,
            $"Private bytes did not settle to a bounded high-water band. {summary}; limit={maxPrivateSpan:F1} MB.");
        Assert.True(handleSpan <= maxHandleSpan,
            $"Handle count did not settle. {summary}; limit={maxHandleSpan}.");
        Assert.True(threadSpan <= maxThreadSpan,
            $"Thread count did not settle. {summary}; limit={maxThreadSpan}.");
    }

    private void CaptureManagedSnapshot(Process process, string scenario, string point)
    {
        var directory = Environment.GetEnvironmentVariable(SnapshotDirectoryVariable);
        var tool = Environment.GetEnvironmentVariable(GcDumpToolVariable);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(tool)) return;

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{scenario}-{point}.gcdump");
        RunDiagnosticTool(
            tool,
            ["collect", "-p", process.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture), "-o", path, "-t", "120"]);
        output.WriteLine($"{scenario}: managed snapshot captured at {point}: {path}");
    }

    private void CaptureHeapDump(Process process, string scenario, string point)
    {
        var directory = Environment.GetEnvironmentVariable(SnapshotDirectoryVariable);
        var tool = Environment.GetEnvironmentVariable(HeapDumpToolVariable);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(tool)) return;

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{scenario}-{point}.dmp");
        RunDiagnosticTool(
            tool,
            ["collect", "-p", process.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture), "-o", path, "--type", "Heap"]);
        output.WriteLine($"{scenario}: heap dump captured at {point}: {path}");
    }

    private static void RunDiagnosticTool(string tool, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var capture = Process.Start(startInfo);
        Assert.NotNull(capture);
        var stdout = capture.StandardOutput.ReadToEndAsync();
        var stderr = capture.StandardError.ReadToEndAsync();
        Assert.True(capture.WaitForExit(180_000), $"{Path.GetFileName(tool)} timed out.");
        Assert.True(capture.ExitCode == 0,
            $"{Path.GetFileName(tool)} failed ({capture.ExitCode}). " +
            $"{stdout.GetAwaiter().GetResult()} {stderr.GetAwaiter().GetResult()}");
    }

    private static AutomationElement? FindConversationButton(Window window)
    {
        var found = FindFirst(window, query => query.ByAutomationId("SessionButton"))
            ?? FindFirst(window, query => query.ByAutomationId("PinnedSessionButton"));
        if (found is not null) return found;

        foreach (var expander in FindAll(window, query => query.ByName("Expand or collapse chats")))
        {
            Invoke(expander);
            Thread.Sleep(150);
            found = FindFirst(window, query => query.ByAutomationId("SessionButton"));
            if (found is not null) return found;
        }

        return null;
    }

    private static AutomationElement[] FindConversationButtons(Window window)
    {
        var found = FindAll(window, query => query.ByAutomationId("SessionButton"));
        if (found.Length >= 2) return found;

        foreach (var expander in FindAll(window, query => query.ByName("Expand or collapse chats")))
        {
            Invoke(expander);
            Thread.Sleep(150);
            found = FindAll(window, query => query.ByAutomationId("SessionButton"));
            if (found.Length >= 2) return found;
        }
        return found;
    }

    private static AutomationElement? FindAgentButton(Window window)
    {
        // Sidebar data is loaded asynchronously after the window appears. A scenario selected on
        // its own cannot rely on Settings/Add Agent warmups having hidden that startup latency.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var found = FindFirst(window, query => query.ByAutomationId("AgentButton"), attempts: 1);
            if (found is not null) return found;
            Thread.Sleep(100);
        }
        return null;
    }

    private static void InvokeByAutomationId(Window window, string automationId)
    {
        var element = FindFirst(window, query => query.ByAutomationId(automationId), attempts: 100);
        Assert.NotNull(element);
        Invoke(element);
    }

    private static void InvokeByName(Window window, string name)
    {
        var element = FindFirst(window, query => query.ByName(name), attempts: 100);
        Assert.NotNull(element);
        Invoke(element);
    }

    private static void Invoke(AutomationElement element)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                element.AsButton().Invoke();
                return;
            }
            catch (COMException) when (attempt < 9)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static AutomationElement? FindFirst(
        Window window,
        Func<FlaUI.Core.Conditions.ConditionFactory, FlaUI.Core.Conditions.ConditionBase> condition,
        int attempts = 20)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                var found = window.FindFirstDescendant(condition);
                if (found is not null) return found;
            }
            catch (COMException)
            {
                // The UIA tree changed while navigating; retry against the new tree.
            }
            Thread.Sleep(100);
        }
        return null;
    }

    private static AutomationElement[] FindAll(Window window, Func<FlaUI.Core.Conditions.ConditionFactory, FlaUI.Core.Conditions.ConditionBase> condition)
    {
        try { return window.FindAllDescendants(condition); }
        catch (COMException) { return Array.Empty<AutomationElement>(); }
    }

    private static double LinearSlope(IReadOnlyList<double> values)
    {
        var xMean = (values.Count - 1) / 2.0;
        var yMean = values.Average();
        double numerator = 0;
        double denominator = 0;
        for (var index = 0; index < values.Count; index++)
        {
            var dx = index - xMean;
            numerator += dx * (values[index] - yMean);
            denominator += dx * dx;
        }
        return denominator == 0 ? 0 : numerator / denominator;
    }

    private static int ReadPositiveInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static double ReadPositiveDouble(string name, double fallback)
        => double.TryParse(
               Environment.GetEnvironmentVariable(name),
               System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture,
               out var value) && value > 0
            ? value
            : fallback;

    private static HashSet<string> ReadScenarios()
    {
        var configured = Environment.GetEnvironmentVariable("CONNECTONION_MEMORY_SCENARIOS");
        if (string.IsNullOrWhiteSpace(configured))
            return new(StringComparer.OrdinalIgnoreCase)
            {
                "settings", "add-agent", "agent-detail", "conversation", "conversation-alternating",
            };

        var scenarios = configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = scenarios.Except(new[]
            { "settings", "add-agent", "agent-detail", "conversation", "conversation-alternating" },
            StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(unknown.Length == 0,
            $"Unknown CONNECTONION_MEMORY_SCENARIOS value(s): {string.Join(", ", unknown)}.");
        return scenarios;
    }

    private sealed record MemorySample(
        int Cycle, double PrivateMb, double WorkingSetMb, int Handles, int Threads)
    {
        public static MemorySample Capture(Process process, int cycle)
        {
            process.Refresh();
            return new MemorySample(
                cycle,
                process.PrivateMemorySize64 / 1048576d,
                process.WorkingSet64 / 1048576d,
                process.HandleCount,
                process.Threads.Count);
        }

        public override string ToString()
            => $"private={PrivateMb:F1}MB working={WorkingSetMb:F1}MB handles={Handles} threads={Threads}";
    }
}
