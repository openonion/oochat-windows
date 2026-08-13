using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ConnectOnion.WinUIClient.Data;
using ConnectOnion.WinUIClient.Models;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.Data.Sqlite;

namespace ConnectOnion.WinUIClient.UITests;

/// <summary>
/// Opt-in, real-window operation timings used by the release audit. The test writes machine-
/// readable JSON only when CONNECTONION_UI_PERF_OUT is set; normal CI discovery is a no-op.
/// </summary>
public sealed class PerformanceAuditTests(ITestOutputHelper output)
{
    private const string OutputVariable = "CONNECTONION_UI_PERF_OUT";
    // Deliberately looser than the ratified medians in PERFORMANCE_AUDIT_2026-07-25_EN.md.
    // These are regression ceilings, not aspirational targets: ordinary machine noise must pass,
    // while a lost virtualization boundary or accidental full-tree rebuild must fail loudly.
    private const double MaxShellOperationMedianMs = 1_000;
    private const double MaxConversationFirstOpenMs = 1_500;
    private const double MaxConversationCachedReopenMedianMs = 1_000;
    private const double MaxToolExpandMedianMs = 250;
    private const int MaxRealizedTranscriptItems = 64;
    private const double MaxLargeConversationPrivateBytesMb = 450;
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
    };
    private const int WmClose = 0x0010;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [Fact]
    public async Task ReleaseUiOperations_AreMeasuredAgainstSyntheticLargeHistories()
    {
        var outputPath = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(outputPath)) return;

        var executable = Environment.GetEnvironmentVariable(ShellSmokeTests.ExecutableEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(executable));
        Assert.Contains("release", executable, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x64", executable, StringComparison.OrdinalIgnoreCase);

        var conversations = await EnsurePerformanceConversationsAsync([100, 500, 2000]);
        ProbePersistedToolActivity();

        var result = new AuditResult
        {
            Configuration = "Release",
            Platform = "x64",
            Packaged = false,
            TimingResolutionMs = 20,
            DataSet = "Synthetic clone of isolated real profile; mixed normal, markdown, activity, tool activity, interactive, diff, and attachment metadata.",
        };

        MeasureShellOperations(result);
        foreach (var conversation in conversations)
            MeasureConversation(result, conversation);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, ReportJsonOptions));
        AssertPerformanceBudgets(result);
    }

    private void AssertPerformanceBudgets(AuditResult result)
    {
        foreach (var operation in result.Operations)
        {
            output.WriteLine($"{operation.Name}: {operation.MedianMs:N1} ms median");
            Assert.True(
                operation.MedianMs <= MaxShellOperationMedianMs,
                $"{operation.Name} median {operation.MedianMs:N1} ms exceeded "
                + $"the {MaxShellOperationMedianMs:N0} ms regression ceiling.");
        }

        foreach (var conversation in result.Conversations)
        {
            output.WriteLine(
                $"{conversation.MessageCount} messages: first {conversation.FirstOpenMs:N1} ms, "
                + $"cached {conversation.CachedReopen.MedianMs:N1} ms, "
                + $"realized {conversation.RealizedListItems}, private {conversation.PrivateBytesMb:N1} MB");
            Assert.True(
                conversation.FirstOpenMs <= MaxConversationFirstOpenMs,
                $"{conversation.MessageCount}-message first open exceeded "
                + $"{MaxConversationFirstOpenMs:N0} ms.");
            Assert.True(
                conversation.CachedReopen.MedianMs <= MaxConversationCachedReopenMedianMs,
                $"{conversation.MessageCount}-message cached reopen exceeded "
                + $"{MaxConversationCachedReopenMedianMs:N0} ms.");
            Assert.True(
                conversation.RealizedListItems <= MaxRealizedTranscriptItems,
                $"{conversation.MessageCount}-message transcript realized "
                + $"{conversation.RealizedListItems} rows; virtualization ceiling is "
                + $"{MaxRealizedTranscriptItems}.");
            if (conversation.ToolActivityExpand is { } expansion)
            {
                Assert.True(
                    expansion.MedianMs <= MaxToolExpandMedianMs,
                    $"Tool Activity expansion median {expansion.MedianMs:N1} ms exceeded "
                    + $"{MaxToolExpandMedianMs:N0} ms.");
            }
        }

        var largest = result.Conversations.MaxBy(conversation => conversation.MessageCount);
        Assert.NotNull(largest);
        Assert.True(
            largest.PrivateBytesMb <= MaxLargeConversationPrivateBytesMb,
            $"{largest.MessageCount}-message private bytes {largest.PrivateBytesMb:N1} MB exceeded "
            + $"the {MaxLargeConversationPrivateBytesMb:N0} MB regression ceiling.");
    }

    private void MeasureShellOperations(AuditResult result)
    {
        using var launched = ShellSmokeTests.LaunchApp(handleFirstRunDialog: false);
        Assert.NotNull(launched);
        var window = launched.Window;

        var settings = new List<double>();
        var addAgent = new List<double>();
        for (var index = 0; index < 10; index++)
        {
            settings.Add(MeasureUntil(
                () => ShellSmokeTests.OpenSettings(window),
                () => FindByAutomationId(window, "GeneralNav") is not null));
            Invoke(WaitForName(window, "Close settings"));
            Assert.True(WaitUntil(() => FindByAutomationId(window, "GeneralNav") is null));

            addAgent.Add(MeasureUntil(
                () => Invoke(WaitForAutomationId(window, "AddAgentButton")),
                () => FindByAutomationId(window, "AgentAddressInput") is not null));
            Invoke(WaitForName(window, "Cancel adding agent"));
            Assert.True(WaitUntil(() => FindByAutomationId(window, "AgentAddressInput") is null));
        }

        result.Operations.Add(OperationStats.From("Open Settings", settings));
        result.Operations.Add(OperationStats.From("Open Add Agent", addAgent));

        var minimizeRestore = new List<double>();
        for (var index = 0; index < 10; index++)
        {
            ShowWindow(launched.Process.MainWindowHandle, SwMinimize);
            Assert.True(WaitUntil(() => IsIconic(launched.Process.MainWindowHandle)));
            minimizeRestore.Add(MeasureUntil(
                () => ShowWindow(launched.Process.MainWindowHandle, SwRestore),
                () => !IsIconic(launched.Process.MainWindowHandle)
                      && IsWindowVisible(launched.Process.MainWindowHandle)
                      && launched.Process.Responding));
        }
        result.Operations.Add(OperationStats.From("Restore minimized window", minimizeRestore));

        // WM_CLOSE is the product's real hide-to-tray path. A redirected second launch calls the
        // same BringToForeground method as the tray Open command, so this measures the restore
        // path without automating the notification-area overflow UI.
        var trayRestore = new List<double>();
        for (var index = 0; index < 10; index++)
        {
            PostMessage(launched.Process.MainWindowHandle, WmClose, IntPtr.Zero, IntPtr.Zero);
            Assert.True(WaitUntil(() => !IsWindowVisible(launched.Process.MainWindowHandle)));
            trayRestore.Add(MeasureUntil(
                () =>
                {
                    using var redirected = Process.Start(new ProcessStartInfo(
                        Environment.GetEnvironmentVariable(ShellSmokeTests.ExecutableEnvironmentVariable)!)
                    {
                        UseShellExecute = true,
                    });
                },
                () => IsWindowVisible(launched.Process.MainWindowHandle)
                      && launched.Process.Responding));
        }
        result.Operations.Add(OperationStats.From("Restore hidden/tray window", trayRestore));
    }

    private void ProbePersistedToolActivity()
    {
        using var connection = new SqliteConnection(
            $"Data Source={AppStorage.PathFor("connectonion.db")};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_args
            FROM messages
            WHERE event_kind = 'tool_activity'
              AND conversation_id NOT LIKE 'performance-%'
            LIMIT 1;
            """;
        var payload = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(payload)) return;
        try
        {
            var restored = JsonSerializer.Deserialize<ToolActivityViewModel>(payload);
            output.WriteLine(
                $"Persisted Tool Activity probe restored {restored?.Steps.Count ?? 0} steps.");
        }
        catch (Exception exception)
        {
            output.WriteLine($"Persisted Tool Activity probe failed: {exception}");
        }
    }

    private void MeasureConversation(AuditResult result, PerformanceConversation conversation)
    {
        using var launched = ShellSmokeTests.LaunchApp(handleFirstRunDialog: false);
        Assert.NotNull(launched);
        var window = launched.Window;

        var firstOpen = MeasureOpenConversation(window, conversation.Title);
        var messageList = WaitForAutomationId(window, "MessageList");
        var realized = FindAll(messageList, query => query.ByControlType(ControlType.ListItem)).Length;
        launched.Process.Refresh();

        var cached = new List<double>();
        for (var index = 0; index < 5; index++)
        {
            Invoke(WaitForAutomationId(window, "AgentsNavigationButton"));
            Assert.True(WaitUntil(() => FindByAutomationId(window, "HomeAddAgentButton") is not null));
            cached.Add(MeasureOpenConversation(window, conversation.Title));
        }

        var toolExpands = new List<double>();
        var visibleButtons = FindAll(window, query => query.ByControlType(ControlType.Button));
        var toolButton = visibleButtons
            .FirstOrDefault(element =>
                element.Properties.Name.ValueOrDefault?.Contains(
                    "Tool activity.", StringComparison.Ordinal) == true
                && element.Properties.Name.ValueOrDefault?.Contains(
                    "Expand", StringComparison.Ordinal) == true);
        if (toolButton is null)
        {
            output.WriteLine(
                $"No collapsed Tool Activity was realized for {conversation.MessageCount} messages. "
                + $"Visible button names: {string.Join(" | ", visibleButtons.Select(
                    element => element.Properties.Name.ValueOrDefault))}");
        }
        if (toolButton is not null)
        {
            toolExpands.Add(MeasureUntil(
                () => Invoke(toolButton),
                () => toolButton.Properties.Name.ValueOrDefault?.EndsWith(
                    "Collapse", StringComparison.Ordinal) == true));
        }

        result.Conversations.Add(new ConversationResult
        {
            MessageCount = conversation.MessageCount,
            FirstOpenMs = Math.Round(firstOpen, 1),
            CachedReopen = OperationStats.From("Cached reopen", cached),
            RealizedListItems = realized,
            WorkingSetMb = Math.Round(launched.Process.WorkingSet64 / 1048576d, 1),
            PrivateBytesMb = Math.Round(launched.Process.PrivateMemorySize64 / 1048576d, 1),
            HandleCount = launched.Process.HandleCount,
            ThreadCount = launched.Process.Threads.Count,
            ToolActivityExpand = toolExpands.Count == 0
                ? null
                : OperationStats.From("Expand Tool Activity", toolExpands),
        });
    }

    private static double MeasureOpenConversation(Window window, string title)
        => MeasureUntil(
            () => Invoke(WaitForSession(window, title)),
            () =>
            {
                var list = FindByAutomationId(window, "MessageList");
                return list is not null
                       && FindAll(list, query => query.ByControlType(ControlType.ListItem)).Length > 0;
            },
            timeoutSeconds: 20);

    private static async Task<IReadOnlyList<PerformanceConversation>>
        EnsurePerformanceConversationsAsync(IReadOnlyList<int> sizes)
    {
        var sessions = new SessionRepository();
        var conversations = new ConversationRepository();
        var state = await sessions.LoadAsync();
        Assert.NotEmpty(state.Sessions);

        var sourceSession = state.Sessions.FirstOrDefault(session => session.Id == state.ActiveSessionId)
                            ?? state.Sessions[0];
        var sourceMessages = await conversations.LoadMessagesAsync(sourceSession.Id);
        Assert.NotEmpty(sourceMessages);
        var toolSource = sourceMessages.FirstOrDefault(message => message.EventKind == "tool_activity");
        if (toolSource is null)
        {
            foreach (var session in state.Sessions.Where(session => session.Id != sourceSession.Id))
            {
                var candidateMessages = await conversations.LoadMessagesAsync(session.Id);
                toolSource = candidateMessages.FirstOrDefault(
                    message => message.EventKind == "tool_activity");
                if (toolSource is not null) break;
            }
        }

        var created = new List<PerformanceConversation>(sizes.Count);
        var fixtures = new List<(string ConversationId, IReadOnlyList<ChatMessage> Messages)>(sizes.Count);
        foreach (var size in sizes)
        {
            var id = $"performance-{size}";
            var title = $"Performance {size} messages";
            var existing = state.Sessions.FirstOrDefault(session => session.Id == id);
            if (existing is null)
            {
                existing = new SessionSummary
                {
                    Id = id,
                    AgentId = sourceSession.AgentId,
                    Title = title,
                    Mode = sourceSession.Mode,
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                    UpdatedAt = DateTime.UtcNow.ToString("o"),
                };
                state.Sessions.Add(existing);
            }

            var messages = new List<ChatMessage>(size);
            for (var index = 0; index < size; index++)
            {
                var source = sourceMessages[index % sourceMessages.Count];
                var clone = CloneMessage(source, index + 1);
                if (index % 25 == 7 && clone.Role == ChatRole.Agent && string.IsNullOrEmpty(clone.EventKind))
                {
                    clone.Content = """
                        ## Performance fixture

                        A long Markdown response with **emphasis**, a [link](https://example.com),
                        and enough wrapping text to exercise the real renderer without using user data.

                        ```text
                        one
                        two
                        three
                        ```
                        """;
                }
                messages.Add(clone);
            }
            if (toolSource is not null)
            {
                messages[0] = CloneMessage(toolSource, 1);
                messages[^1] = CloneMessage(toolSource, size);
            }

            created.Add(new PerformanceConversation(id, title, size));
            fixtures.Add((id, messages));
        }

        // messages.conversation_id references sessions.id. Persist every synthetic session before
        // inserting its transcript; the previous one-pass form tried to write the messages while
        // the new SessionSummary existed only in memory and failed immediately under foreign keys.
        state.ActiveSessionId = sourceSession.Id;
        await sessions.SaveAsync(state);
        foreach (var fixture in fixtures)
        {
            await conversations.UpsertMessagesAsync(fixture.ConversationId, fixture.Messages);
        }

        // The tray timing loop sends WM_CLOSE directly. Make that operation deterministic for
        // an isolated assessment profile: the product now asks on first close, so leaving the
        // default here opens a modal dialog and the window correctly remains visible. Persisting
        // the explicit tray choice exercises the intended hide/redirected-launch restore path.
        var preferences = new PreferencesRepository();
        var snapshot = await preferences.LoadAsync();
        snapshot.CloseBehavior = WindowCloseBehavior.HideToTray;
        await preferences.SaveAsync(snapshot);
        return created;
    }

    private static ChatMessage CloneMessage(ChatMessage source, long id)
    {
        var clone = new ChatMessage
        {
            Id = id,
            Role = source.Role,
            Content = source.Content,
            AgentName = source.AgentName,
            EventKind = source.EventKind,
            EventKey = source.EventKey,
            EventEyebrow = source.EventEyebrow,
            EventTitle = source.EventTitle,
            EventDetail = source.EventDetail,
            EventMeta = source.EventMeta,
            EventArgs = source.EventArgs,
            EventResult = source.EventResult,
            Status = source.Status,
            IsOnboarding = source.IsOnboarding,
            CreatedAtUnixMs = source.CreatedAtUnixMs + id,
            ToolActivity = source.ToolActivity,
        };
        foreach (var attachment in source.Attachments)
        {
            clone.Attachments.Add(new ChatAttachment
            {
                Id = $"{id}-{attachment.Id}",
                Kind = attachment.Kind,
                FileName = attachment.FileName,
                MimeType = attachment.MimeType,
                SizeBytes = attachment.SizeBytes,
                LocalCachePath = attachment.LocalCachePath,
                RemoteUri = attachment.RemoteUri,
                Status = attachment.Status,
                Error = attachment.Error,
            });
        }
        return clone;
    }

    private static double MeasureUntil(Action action, Func<bool> completed, int timeoutSeconds = 10)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        Assert.True(WaitUntil(completed, timeoutSeconds));
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static bool WaitUntil(Func<bool> predicate, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (predicate()) return true;
            }
            catch (COMException)
            {
                // UIA queried while the XAML tree was being replaced; retry the new tree.
            }
            Thread.Sleep(20);
        }
        return false;
    }

    private static AutomationElement WaitForAutomationId(Window window, string automationId)
    {
        AutomationElement? found = null;
        Assert.True(WaitUntil(() => (found = FindByAutomationId(window, automationId)) is not null));
        return found!;
    }

    private static AutomationElement WaitForName(Window window, string name)
    {
        AutomationElement? found = null;
        Assert.True(WaitUntil(() => (found = FindByName(window, name)) is not null));
        return found!;
    }

    private static AutomationElement WaitForSession(Window window, string title)
    {
        AutomationElement? found = null;
        Assert.True(WaitUntil(() =>
        {
            found = FindAll(window, query => query.ByAutomationId("SessionButton"))
                .Concat(FindAll(window, query => query.ByAutomationId("PinnedSessionButton")))
                .FirstOrDefault(element =>
                    element.Properties.Name.ValueOrDefault?.StartsWith(
                        title + ",", StringComparison.Ordinal) == true);
            return found is not null;
        }));
        return found!;
    }

    private static AutomationElement? FindByAutomationId(Window window, string automationId)
    {
        try { return window.FindFirstDescendant(query => query.ByAutomationId(automationId)); }
        catch (COMException) { return null; }
    }

    private static AutomationElement? FindByName(Window window, string name)
    {
        try { return window.FindFirstDescendant(query => query.ByName(name)); }
        catch (COMException) { return null; }
    }

    private static AutomationElement[] FindAll(
        AutomationElement root,
        Func<FlaUI.Core.Conditions.ConditionFactory,
            FlaUI.Core.Conditions.ConditionBase> condition)
    {
        try { return root.FindAllDescendants(condition); }
        catch (COMException) { return []; }
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
                Thread.Sleep(50);
            }
        }
    }

    private sealed record PerformanceConversation(string Id, string Title, int MessageCount);

    private sealed class AuditResult
    {
        public string Configuration { get; set; } = "";
        public string Platform { get; set; } = "";
        public bool Packaged { get; set; }
        public int TimingResolutionMs { get; set; }
        public string DataSet { get; set; } = "";
        public List<OperationStats> Operations { get; } = [];
        public List<ConversationResult> Conversations { get; } = [];
    }

    private sealed class ConversationResult
    {
        public int MessageCount { get; set; }
        public double FirstOpenMs { get; set; }
        public OperationStats CachedReopen { get; set; } = null!;
        public int RealizedListItems { get; set; }
        public double WorkingSetMb { get; set; }
        public double PrivateBytesMb { get; set; }
        public int HandleCount { get; set; }
        public int ThreadCount { get; set; }
        public OperationStats? ToolActivityExpand { get; set; }
    }

    private sealed class OperationStats
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public double MedianMs { get; set; }
        public double MinMs { get; set; }
        public double MaxMs { get; set; }

        public static OperationStats From(string name, IReadOnlyList<double> values)
        {
            var sorted = values.Order().ToArray();
            var middle = sorted.Length / 2;
            var median = sorted.Length % 2 == 0
                ? (sorted[middle - 1] + sorted[middle]) / 2
                : sorted[middle];
            return new OperationStats
            {
                Name = name,
                Count = sorted.Length,
                MedianMs = Math.Round(median, 1),
                MinMs = Math.Round(sorted[0], 1),
                MaxMs = Math.Round(sorted[^1], 1),
            };
        }
    }
}
