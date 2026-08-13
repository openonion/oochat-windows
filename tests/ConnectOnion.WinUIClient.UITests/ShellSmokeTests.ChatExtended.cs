using System.Net.WebSockets;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.Data.Sqlite;

namespace ConnectOnion.WinUIClient.UITests;

public sealed partial class ShellSmokeTests
{
    private const string SecondarySessionId = "ui-chat-session-secondary";
    private const string SecondaryTitle = "Automation Secondary Chat";
    private const string NotificationAgentEnvironmentVariable =
        "CONNECTONION_UI_NOTIFICATION_AGENT_ID";
    private const string NotificationConversationEnvironmentVariable =
        "CONNECTONION_UI_NOTIFICATION_CONVERSATION_ID";

    [Fact]
    public async Task Navigation_HomeAgentChatAndAgentsLibrary_AllOpen()
    {
        await using var server = new UiFakeAgentServer();
        if (!PrepareChatProfile(server.BaseUri)) return;

        try
        {
            using var launched = LaunchApp(handleFirstRunDialog: false);
            if (launched is null) return;
            var window = launched.Window;

            Assert.NotNull(WaitForDescendant(window, "HomeAddAgentButton"));
            OpenAgentDetail(window);
            Assert.NotNull(WaitForDescendant(window, "SuggestionButton"));

            var session = FindSessionByTitle(window, ChatTitle, TimeSpan.FromSeconds(15));
            Assert.NotNull(session);
            session!.AsButton().Invoke();
            Assert.NotNull(WaitForDescendant(window, "MessageList", TimeSpan.FromSeconds(20)));

            var agents = WaitForDescendant(window, "AgentsNavigationButton");
            Assert.NotNull(agents);
            agents.AsButton().Invoke();
            Assert.NotNull(WaitForDescendant(window, "HomeAddAgentButton"));
        }
        finally
        {
            RemoveChatFixture();
        }
    }

    [Fact]
    public async Task Navigation_BackForwardRestoresEntities_ClosesFind_AndSurvivesRapidUse()
    {
        await using var server = new UiFakeAgentServer();
        if (!PrepareChatProfile(server.BaseUri)) return;

        try
        {
            using var launched = LaunchApp(handleFirstRunDialog: false);
            if (launched is null) return;
            var window = launched.Window;

            Assert.NotNull(WaitForDescendant(window, "HomeAddAgentButton"));
            OpenAgentDetail(window);
            Assert.NotNull(WaitForDescendant(window, "SuggestionButton"));

            var session = FindSessionByTitle(window, ChatTitle, TimeSpan.FromSeconds(15));
            Assert.NotNull(session);
            session!.AsButton().Invoke();
            Assert.NotNull(WaitForDescendant(window, "MessageList", TimeSpan.FromSeconds(20)));

            OpenFindOverlay(window);

            var agents = WaitForDescendant(window, "AgentsNavigationButton");
            Assert.NotNull(agents);
            agents.AsButton().Invoke();
            Assert.NotNull(WaitForDescendant(window, "HomeAddAgentButton"));
            Assert.Null(window.FindFirstDescendant(query => query.ByAutomationId("FindTextBox")));

            var back = WaitForDescendant(window, "BackButton");
            var forward = WaitForDescendant(window, "ForwardButton");
            Assert.NotNull(back);
            Assert.NotNull(forward);
            Assert.True(WaitUntil(() => back.IsEnabled));

            back.AsButton().Invoke();
            Assert.NotNull(WaitForDescendant(window, "MessageList", TimeSpan.FromSeconds(20)));
            back.AsButton().Invoke();
            Assert.NotNull(WaitForDescendant(window, "SuggestionButton", TimeSpan.FromSeconds(20)));
            back.AsButton().Invoke();
            Assert.NotNull(WaitForDescendant(window, "HomeAddAgentButton", TimeSpan.FromSeconds(20)));
            Assert.True(WaitUntil(() => forward.IsEnabled));

            for (var cycle = 0; cycle < 5; cycle++)
            {
                forward.AsButton().Invoke();
                Assert.NotNull(WaitForDescendant(window, "SuggestionButton", TimeSpan.FromSeconds(10)));
                Assert.True(WaitUntil(() => back.IsEnabled));
                back.AsButton().Invoke();
                Assert.NotNull(WaitForDescendant(window, "HomeAddAgentButton", TimeSpan.FromSeconds(10)));
                Assert.True(WaitUntil(() => forward.IsEnabled));
            }

            launched.Process.Refresh();
            Assert.False(launched.Process.HasExited);
            Assert.True(launched.Process.Responding);
        }
        finally
        {
            RemoveChatFixture();
        }
    }

    [Fact]
    public async Task AgentDetail_SuggestionTemplate_PopulatesComposerDraft()
    {
        await using var server = new UiFakeAgentServer();
        if (!PrepareChatProfile(server.BaseUri)) return;

        try
        {
            using var launched = LaunchApp(handleFirstRunDialog: false);
            if (launched is null) return;
            var window = launched.Window;
            OpenAgentDetail(window);

            var suggestion = WaitForDescendant(window, "SuggestionButton");
            Assert.NotNull(suggestion);
            suggestion.AsButton().Invoke();

            var input = WaitForDescendant(window, "MessageInput");
            Assert.NotNull(input);
            const string expected =
                "Summarize your capabilities and give me a few concrete examples.";
            Assert.True(WaitUntil(() =>
                WaitForDescendant(window, "MessageInput", TimeSpan.FromMilliseconds(250))?
                    .AsTextBox().Text == expected));
            Assert.True(WaitUntil(() => input.Properties.HasKeyboardFocus.ValueOrDefault));
        }
        finally
        {
            RemoveChatFixture();
        }
    }

    [Fact]
    public async Task NewChat_AgentDetailFirstSend_NavigatesToChatAndCompletesTurn()
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdSocket = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new UiFakeAgentServer(async (_, socket, ct) =>
        {
            await UiFakeAgentServer.ReceiveFrameOfTypeAsync(socket, "CONNECT", ct);
            await UiFakeAgentServer.SendTextAsync(socket,
                "{\"type\":\"CONNECTED\",\"session_id\":\"keyboard-session\",\"status\":\"connected\"}", ct);
            received.TrySetResult(await UiFakeAgentServer.ReceiveFrameOfTypeAsync(socket, "INPUT", ct));
            await UiFakeAgentServer.SendTextAsync(socket,
                "{\"type\":\"OUTPUT\",\"result\":\"Keyboard reply\"}", ct);
            await holdSocket.Task.WaitAsync(ct);
        });

        if (!PrepareChatProfile(server.BaseUri)) return;
        try
        {
            using var launched = LaunchApp(handleFirstRunDialog: false);
            if (launched is null) return;
            var window = launched.Window;
            Assert.Equal(1, CountAgentSessions());

            window.SetForeground();
            var fileMenu = WaitForDescendant(window, "FileMenuButton");
            Assert.NotNull(fileMenu);
            fileMenu!.Click();
            var newChat = WaitForElement(
                launched.Automation.GetDesktop(), "NewChatMenuItem", TimeSpan.FromSeconds(5));
            Assert.NotNull(newChat);
            // The MenuBarItem itself needs a physical click to reveal its flyout, but the
            // MenuFlyoutItem exposes InvokePattern. Invoking the command avoids a second desktop
            // coordinate click that can be stolen when a hosted runner changes foreground
            // ownership while the flyout is open.
            newChat!.AsMenuItem().Invoke();
            Assert.NotNull(WaitForDescendant(window, "SuggestionButton", TimeSpan.FromSeconds(20)));
            Assert.NotNull(WaitForDescendant(window, "MessageInput", TimeSpan.FromSeconds(20)));
            Assert.Null(window.FindFirstDescendant(query => query.ByAutomationId("MessageList")));
            Assert.Equal(1, CountAgentSessions());

            var inputElement = WaitForDescendant(window, "MessageInput");
            Assert.NotNull(inputElement);
            var input = inputElement!.AsTextBox();
            Assert.True(WaitUntil(() => input.IsEnabled));

            window.SetForeground();
            input.Click();
            Assert.True(WaitUntil(() => input.Properties.HasKeyboardFocus.ValueOrDefault));
            // WinUI can report UIA focus one dispatcher tick before the text service is ready to
            // consume SendInput. Give the real editor that tick so the first character is not
            // lost on slower desktop sessions.
            Thread.Sleep(300);
            // Keep this layout-independent: SPACE can be consumed by the runner's active IME,
            // while letters, hyphens and Enter map consistently across Windows CI desktops.
            const string prompt = "keyboard-automation-prompt";
            TypeWithDelay(prompt);
            Assert.True(
                WaitUntil(() => input.Text == prompt),
                $"Expected keyboard input '{prompt}', actual '{input.Text}'.");
            var send = WaitForDescendant(window, "SendMessageButton");
            Assert.NotNull(send);
            Assert.True(WaitUntil(() => send.IsEnabled));
            // Submit through the focused editor, not the pointer-accessible button. This is the
            // end-to-end keyboard contract: the same Enter gesture a user relies on must cross
            // the composer, page, run manager and WebSocket without moving focus.
            window.SetForeground();
            input.Click();
            Assert.True(WaitUntil(() => input.Properties.HasKeyboardFocus.ValueOrDefault));
            Keyboard.Type(VirtualKeyShort.RETURN);

            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(
                ReferenceEquals(completed, received.Task),
                $"Enter did not submit; composer text is '{input.Text}' and focused={input.Properties.HasKeyboardFocus.ValueOrDefault}.");
            var inputJson = await received.Task;
            Assert.Contains(prompt, inputJson, StringComparison.Ordinal);
            Assert.NotNull(WaitForDescendant(window, "MessageList", TimeSpan.FromSeconds(20)));
            Assert.NotNull(WaitForAccessibleName(
                window, "Keyboard reply", TimeSpan.FromSeconds(20)));
            var chatInput = WaitForDescendant(window, "MessageInput", TimeSpan.FromSeconds(20));
            Assert.NotNull(chatInput);
            Assert.True(
                WaitUntil(() => chatInput!.Properties.HasKeyboardFocus.ValueOrDefault),
                "the composer did not keep keyboard focus after Enter submitted the message");
            Assert.Equal(2, CountAgentSessions());
        }
        finally
        {
            holdSocket.TrySetResult();
            RemoveChatFixture();
        }
    }

    [Fact]
    public async Task Chat_StopResponse_DisablesImmediatelyAndSettlesExactlyOnce()
    {
        var turnStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOutput = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var holdSocket = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new UiFakeAgentServer(async (_, socket, ct) =>
        {
            await UiFakeAgentServer.ReceiveFrameOfTypeAsync(socket, "CONNECT", ct);
            await UiFakeAgentServer.SendTextAsync(socket,
                "{\"type\":\"CONNECTED\",\"session_id\":\"stop-session\",\"status\":\"connected\"}", ct);
            await UiFakeAgentServer.ReceiveFrameOfTypeAsync(socket, "INPUT", ct);
            await UiFakeAgentServer.SendTextAsync(socket,
                "{\"type\":\"thinking\",\"id\":\"stop-thinking\",\"content\":\"Working\"}", ct);
            turnStarted.TrySetResult();
            await UiFakeAgentServer.ReceiveFrameOfTypeAsync(socket, "INTERRUPT", ct);
            interruptReceived.TrySetResult();
            await releaseOutput.Task.WaitAsync(ct);
            await UiFakeAgentServer.SendTextAsync(socket,
                "{\"type\":\"OUTPUT\",\"result\":\"Stopped safely\"}", ct);
            await holdSocket.Task.WaitAsync(ct);
        });

        if (!PrepareChatProfile(server.BaseUri)) return;
        try
        {
            using var launched = LaunchApp(handleFirstRunDialog: false);
            if (launched is null) return;
            var window = launched.Window;
            var input = OpenChat(window);
            input.Text = "start a task I can stop";
            var send = WaitForDescendant(window, "SendMessageButton");
            Assert.NotNull(send);
            Assert.True(WaitUntil(() => send.IsEnabled));
            send.AsButton().Invoke();

            // Before INPUT leaves, the same button means "take back the queued send" rather
            // than "interrupt the running turn". Wait until the fake host has accepted INPUT so
            // this test cannot occasionally click during that short, valid cancellation window.
            await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var stop = WaitForDescendant(window, "StopResponseButton", TimeSpan.FromSeconds(20));
            Assert.NotNull(stop);
            Assert.True(WaitUntil(() => stop.IsEnabled));
            stop.AsButton().Invoke();

            Assert.True(
                WaitUntil(() =>
                {
                    var current = WaitForDescendant(
                        window,
                        "StopResponseButton",
                        TimeSpan.FromMilliseconds(250));
                    return current is not null
                           && !current.IsEnabled
                           && current.Properties.Name.ValueOrDefault?.Contains(
                               "Stopping",
                               StringComparison.OrdinalIgnoreCase) == true;
                }),
                "Stop did not enter its disabled intermediate state");
            await interruptReceived.Task.WaitAsync(TimeSpan.FromSeconds(20));

            releaseOutput.TrySetResult();
            Assert.NotNull(WaitForAccessibleName(window, "Stopped safely", TimeSpan.FromSeconds(20)));
            Assert.True(
                WaitUntil(() => window.FindFirstDescendant(
                    query => query.ByAutomationId("StopResponseButton")) is null),
                "Stop remained visible after the terminal frame");
            server.ThrowIfFaulted();
        }
        finally
        {
            releaseOutput.TrySetResult();
            holdSocket.TrySetResult();
            RemoveChatFixture();
        }
    }

    [Fact]
    public async Task Chat_AgentErrorThenRetry_CompletesOnTheNextInput()
    {
        var inputAttempt = 0;
        var retryReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdSocket = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new UiFakeAgentServer(async (_, socket, ct) =>
        {
            try
            {
                await UiFakeAgentServer.ReceiveFrameOfTypeAsync(socket, "CONNECT", ct);
                await UiFakeAgentServer.SendTextAsync(socket,
                    "{\"type\":\"CONNECTED\",\"session_id\":\"retry-session\",\"status\":\"connected\"}", ct);
                while (true)
                {
                    await UiFakeAgentServer.ReceiveFrameOfTypeAsync(socket, "INPUT", ct);
                    if (Interlocked.Increment(ref inputAttempt) == 1)
                    {
                        await UiFakeAgentServer.SendTextAsync(socket,
                            "{\"type\":\"ERROR\",\"message\":\"planned automation failure\"}", ct);
                        continue;
                    }

                    retryReceived.TrySetResult();
                    await UiFakeAgentServer.SendTextAsync(socket,
                        "{\"type\":\"OUTPUT\",\"result\":\"Retry recovered\"}", ct);
                    await holdSocket.Task.WaitAsync(ct);
                    return;
                }
            }
            catch (WebSocketException)
            {
                // Retry may replace the failed turn's socket; the next server connection resumes
                // the same input-attempt counter.
            }
        });

        if (!PrepareChatProfile(server.BaseUri)) return;
        try
        {
            using var launched = LaunchApp(handleFirstRunDialog: false);
            if (launched is null) return;
            var window = launched.Window;
            var input = OpenChat(window);
            input.Text = "recover this turn";
            var send = WaitForDescendant(window, "SendMessageButton");
            Assert.NotNull(send);
            Assert.True(WaitUntil(() => send.IsEnabled));
            send.AsButton().Invoke();

            Assert.NotNull(WaitForAccessibleName(
                window, "planned automation failure", TimeSpan.FromSeconds(20)));
            var retry = WaitForDescendant(window, "RetryTurnButton", TimeSpan.FromSeconds(15));
            Assert.NotNull(retry);
            Assert.True(WaitUntil(() => retry.IsEnabled));
            retry.AsButton().Invoke();

            await retryReceived.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(WaitForAccessibleName(window, "Retry recovered", TimeSpan.FromSeconds(20)));
            Assert.Equal(2, Volatile.Read(ref inputAttempt));
        }
        finally
        {
            holdSocket.TrySetResult();
            RemoveChatFixture();
        }
    }

    [Fact]
    public async Task Chat_SwitchConversationDuringTurn_ReturnsToOnePersistedReply()
    {
        var inputReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdSocket = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new UiFakeAgentServer(async (_, socket, ct) =>
        {
            await UiFakeAgentServer.ReceiveFrameOfTypeAsync(socket, "CONNECT", ct);
            await UiFakeAgentServer.SendTextAsync(socket,
                "{\"type\":\"CONNECTED\",\"session_id\":\"switch-session\",\"status\":\"connected\"}", ct);
            await UiFakeAgentServer.ReceiveFrameOfTypeAsync(socket, "INPUT", ct);
            inputReceived.TrySetResult();
            await releaseOutput.Task.WaitAsync(ct);
            await UiFakeAgentServer.SendTextAsync(socket,
                "{\"type\":\"OUTPUT\",\"result\":\"Background switch reply\"}", ct);
            await holdSocket.Task.WaitAsync(ct);
        });

        if (!PrepareChatProfile(server.BaseUri)) return;
        SeedAdditionalSession(
            SecondarySessionId,
            SecondaryTitle,
            (1, "user", "Secondary conversation marker"));

        try
        {
            using var launched = LaunchApp(handleFirstRunDialog: false);
            if (launched is null) return;
            var window = launched.Window;
            var input = OpenChat(window);
            input.Text = "finish while I am away";
            var send = WaitForDescendant(window, "SendMessageButton");
            Assert.NotNull(send);
            Assert.True(WaitUntil(() => send.IsEnabled));
            send.AsButton().Invoke();
            await inputReceived.Task.WaitAsync(TimeSpan.FromSeconds(20));

            var secondary = FindSessionByTitle(window, SecondaryTitle, TimeSpan.FromSeconds(15));
            Assert.NotNull(secondary);
            secondary!.AsButton().Invoke();
            Assert.NotNull(WaitForAccessibleName(
                window, "Secondary conversation marker", TimeSpan.FromSeconds(20)));

            releaseOutput.TrySetResult();
            Assert.True(WaitUntil(() =>
                CountMessages(ChatSessionId, "Background switch reply") == 1));

            var original = FindSessionByTitle(window, ChatTitle, TimeSpan.FromSeconds(15));
            Assert.NotNull(original);
            original!.AsButton().Invoke();
            Assert.NotNull(WaitForAccessibleName(
                window, "Background switch reply", TimeSpan.FromSeconds(20)));
            Assert.Equal(1, CountMessages(ChatSessionId, "Background switch reply"));
        }
        finally
        {
            releaseOutput.TrySetResult();
            holdSocket.TrySetResult();
            RemoveChatFixture();
        }
    }

    [Fact]
    public async Task Chat_ProcessRestart_RestoresSentTurnFromSQLite()
    {
        var holdSocket = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new UiFakeAgentServer(async (_, socket, ct) =>
        {
            await UiFakeAgentServer.ReceiveFrameOfTypeAsync(socket, "CONNECT", ct);
            await UiFakeAgentServer.SendTextAsync(socket,
                "{\"type\":\"CONNECTED\",\"session_id\":\"restart-session\",\"status\":\"connected\"}", ct);
            await UiFakeAgentServer.ReceiveFrameOfTypeAsync(socket, "INPUT", ct);
            await UiFakeAgentServer.SendTextAsync(socket,
                "{\"type\":\"OUTPUT\",\"result\":\"Persisted restart reply\"}", ct);
            await holdSocket.Task.WaitAsync(ct);
        });

        if (!PrepareChatProfile(server.BaseUri)) return;
        try
        {
            using (var first = LaunchApp(handleFirstRunDialog: false))
            {
                if (first is null) return;
                var input = OpenChat(first.Window);
                input.Text = "persist across restart";
                var send = WaitForDescendant(first.Window, "SendMessageButton");
                Assert.NotNull(send);
                Assert.True(WaitUntil(() => send.IsEnabled));
                send.AsButton().Invoke();
                Assert.NotNull(WaitForAccessibleName(
                    first.Window, "Persisted restart reply", TimeSpan.FromSeconds(20)));
            }

            using var second = LaunchApp(handleFirstRunDialog: false);
            Assert.NotNull(second);
            OpenChat(second!.Window);
            Assert.NotNull(WaitForAccessibleName(
                second.Window, "persist across restart", TimeSpan.FromSeconds(20)));
            Assert.NotNull(WaitForAccessibleName(
                second.Window, "Persisted restart reply", TimeSpan.FromSeconds(20)));
            Assert.Equal(1, CountMessages(ChatSessionId, "Persisted restart reply"));
        }
        finally
        {
            holdSocket.TrySetResult();
            RemoveChatFixture();
        }
    }

    [Fact]
    public async Task NotificationActivation_ColdStart_OpensTargetConversation()
    {
        await using var server = new UiFakeAgentServer();
        if (!PrepareChatProfile(
            server.BaseUri,
            (1, "agent", "Notification target marker"))) return;
        SeedAdditionalSession(
            SecondarySessionId,
            SecondaryTitle,
            (1, "agent", "Notification decoy marker"));

        try
        {
            var environment = new Dictionary<string, string>
            {
                [NotificationAgentEnvironmentVariable] = ChatAgentId,
                [NotificationConversationEnvironmentVariable] = ChatSessionId,
            };
            using var launched = LaunchApp(handleFirstRunDialog: false, environment);
            if (launched is null) return;

            Assert.NotNull(WaitForDescendant(
                launched.Window, "MessageInput", TimeSpan.FromSeconds(20)));
            var messageList = WaitForDescendant(
                launched.Window, "MessageList", TimeSpan.FromSeconds(20));
            Assert.NotNull(messageList);
            var targetMarker = WaitForAccessibleName(
                messageList!, "Notification target marker", TimeSpan.FromSeconds(20));
            Assert.True(
                targetMarker is not null,
                "Notification target content did not render. Visible UIA names:\n" +
                string.Join("\n", messageList!.FindAllDescendants()
                    .Select(element => element.Properties.Name.ValueOrDefault)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Take(100)));
            Assert.Null(WaitForAccessibleName(
                messageList!, "Notification decoy marker", TimeSpan.FromSeconds(1)));
            Assert.Equal(ChatSessionId, ReadMeta("active_session_id"));
        }
        finally
        {
            RemoveChatFixture();
        }
    }

    [Fact]
    public async Task AgentIcon_ContextMenuRemovesCustomIcon_AndAddFormExposesPicker()
    {
        await using var server = new UiFakeAgentServer();
        if (!PrepareChatProfile(server.BaseUri)) return;
        var iconPath = SeedAgentIcon();

        try
        {
            using var launched = LaunchApp(handleFirstRunDialog: false);
            if (launched is null) return;
            var window = launched.Window;
            var agent = WaitForDescendant(window, "AgentButton", TimeSpan.FromSeconds(15));
            Assert.NotNull(agent);
            window.SetForeground();
            agent!.RightClick();

            var desktop = launched.Automation.GetDesktop();
            Assert.NotNull(WaitForElement(desktop, "ChangeAgentIconMenuItem", TimeSpan.FromSeconds(5)));
            var remove = WaitForElement(desktop, "RemoveAgentIconMenuItem", TimeSpan.FromSeconds(5));
            Assert.NotNull(remove);
            remove!.AsMenuItem().Click();

            Assert.True(WaitUntil(() => ReadAgentIconPath() is null));
            Assert.False(File.Exists(iconPath));

            window.SetForeground();
            agent.RightClick();
            Assert.NotNull(WaitForElement(desktop, "ChangeAgentIconMenuItem", TimeSpan.FromSeconds(5)));
            Thread.Sleep(300);
            Assert.Null(desktop.FindFirstDescendant(
                query => query.ByAutomationId("RemoveAgentIconMenuItem")));
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(300);

            var addAgent = WaitForDescendant(window, "AddAgentButton");
            Assert.NotNull(addAgent);
            addAgent.AsButton().Invoke();
            Assert.NotNull(WaitForDescendant(window, "AgentAddressInput", TimeSpan.FromSeconds(15)));
            var appearance = WaitForDescendant(
                window, "AgentAppearanceExpander", TimeSpan.FromSeconds(15));
            Assert.NotNull(appearance);
            appearance!.Patterns.ExpandCollapse.Pattern.Expand();
            Assert.NotNull(WaitForDescendant(window, "ChooseIconButton", TimeSpan.FromSeconds(15)));
        }
        finally
        {
            RemoveChatFixture();
        }
    }

    [Fact]
    public async Task AgentRename_SettingsPersistsAndRefreshesAfterRestart()
    {
        await using var server = new UiFakeAgentServer();
        if (!PrepareChatProfile(server.BaseUri)) return;
        const string renamedAgent = "My Automation Agent";

        try
        {
            using (var launched = LaunchApp(handleFirstRunDialog: false))
            {
                if (launched is null) return;
                var window = launched.Window;
                OpenSettings(window);
                var agentsNav = WaitForDescendant(window, "AgentsNav");
                Assert.NotNull(agentsNav);
                agentsNav!.AsRadioButton().Click();

                var rename = WaitForDescendant(window, "RenameAgentButton");
                Assert.NotNull(rename);
                rename!.AsButton().Invoke();

                var input = WaitForDescendant(window, "RenameAgentInput");
                Assert.NotNull(input);
                input!.AsTextBox().Text = $"  {renamedAgent}  ";

                var save = WaitForAccessibleName(window, "Save", TimeSpan.FromSeconds(5));
                Assert.NotNull(save);
                save!.AsButton().Invoke();

                Assert.True(WaitUntil(() => ReadAgentName() == renamedAgent));
                Assert.NotNull(WaitForAccessibleName(window, renamedAgent, TimeSpan.FromSeconds(10)));
            }

            using var relaunched = LaunchApp(handleFirstRunDialog: false);
            if (relaunched is null) return;
            var persistedAgent = WaitForDescendant(
                relaunched.Window,
                "AgentButton",
                TimeSpan.FromSeconds(15));
            Assert.NotNull(persistedAgent);
            Assert.Contains(
                renamedAgent,
                persistedAgent!.Properties.Name.ValueOrDefault ?? "",
                StringComparison.Ordinal);
        }
        finally
        {
            RemoveChatFixture();
        }
    }

    private static void OpenAgentDetail(Window window)
    {
        var agent = WaitForDescendant(window, "AgentButton", TimeSpan.FromSeconds(15));
        Assert.True(agent is not null,
            "No agent row reached UI Automation. Visible tree:\n" +
            string.Join("\n", window.FindAllDescendants()
                .Where(element => !string.IsNullOrWhiteSpace(
                    element.Properties.AutomationId.ValueOrDefault))
                .Take(120)
                .Select(element =>
                    $"{element.Properties.AutomationId.ValueOrDefault}: " +
                    $"{element.Properties.Name.ValueOrDefault} " +
                    $"[{element.ControlType}]")));
        agent!.AsButton().Invoke();
        // BalanceButton is intentionally collapsed when the optional OpenOnion account service
        // is unavailable. It cannot be a deterministic page marker for an isolated/offline UI
        // test; the suggestion and composer are the detail page's always-present interaction
        // surface and still prove that navigation completed.
        Assert.NotNull(WaitForDescendant(window, "SuggestionButton", TimeSpan.FromSeconds(15)));
        Assert.NotNull(WaitForDescendant(window, "MessageInput", TimeSpan.FromSeconds(15)));
    }

    private static AutomationElement? FindSessionByTitle(
        Window window,
        string title,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var match = window
                    .FindAllDescendants(query => query.ByAutomationId("SessionButton"))
                    .FirstOrDefault(element =>
                        (element.Properties.Name.ValueOrDefault ?? "")
                            .Contains(title, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
            catch (System.Runtime.InteropServices.COMException) { }
            Thread.Sleep(150);
        }
        return null;
    }

    private static AutomationElement? WaitForElement(
        AutomationElement root,
        string automationId,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var match = root.FindFirstDescendant(query => query.ByAutomationId(automationId));
                if (match is not null) return match;
            }
            catch (System.Runtime.InteropServices.COMException) { }
            Thread.Sleep(100);
        }
        return null;
    }

    private static void SeedAdditionalSession(
        string sessionId,
        string title,
        params (long Id, string Role, string Content)[] messages)
    {
        using var connection = OpenChatFixtureDatabase();
        using var transaction = connection.BeginTransaction();
        using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = """
                INSERT INTO sessions
                    (id, agent_id, title, created_at, updated_at, sort_order, mode, has_custom_title)
                VALUES
                    ($id, $agent, $title, '2026-08-05T00:01:00.0000000Z',
                     '2026-08-05T00:01:00.0000000Z', 1, 'safe', 1);
                """;
            session.Parameters.AddWithValue("$id", sessionId);
            session.Parameters.AddWithValue("$agent", ChatAgentId);
            session.Parameters.AddWithValue("$title", title);
            session.ExecuteNonQuery();
        }

        foreach (var message in messages)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO messages (id, conversation_id, role, content, created_at)
                VALUES ($id, $session, $role, $content, $created_at);
                """;
            insert.Parameters.AddWithValue("$id", message.Id);
            insert.Parameters.AddWithValue("$session", sessionId);
            insert.Parameters.AddWithValue("$role", message.Role);
            insert.Parameters.AddWithValue("$content", message.Content);
            insert.Parameters.AddWithValue("$created_at", 1785888060000L + message.Id);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static int CountMessages(string sessionId, string content)
    {
        using var connection = OpenChatFixtureDatabase();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM messages WHERE conversation_id = $session AND content = $content;";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$content", content);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int CountAgentSessions()
    {
        using var connection = OpenChatFixtureDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions WHERE agent_id = $agent;";
        command.Parameters.AddWithValue("$agent", ChatAgentId);
        return Convert.ToInt32(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void TypeWithDelay(string text)
    {
        foreach (var character in text)
        {
            if (character == ' ')
                Keyboard.Press(VirtualKeyShort.SPACE);
            else
                Keyboard.Type(character.ToString());
            Thread.Sleep(25);
        }
    }

    private static string? ReadMeta(string key)
    {
        using var connection = OpenChatFixtureDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var value = command.ExecuteScalar();
        return value is null or DBNull
            ? null
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string SeedAgentIcon()
    {
        var relativePath = "avatars/ui-automation-icon.png";
        var absolutePath = Path.Combine(
            ResolveDataRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllBytes(
            absolutePath,
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZQmcAAAAASUVORK5CYII="));

        using var connection = OpenChatFixtureDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agents SET icon_path = $path WHERE id = $agent;";
        command.Parameters.AddWithValue("$path", relativePath);
        command.Parameters.AddWithValue("$agent", ChatAgentId);
        command.ExecuteNonQuery();
        return absolutePath;
    }

    private static string? ReadAgentIconPath()
    {
        using var connection = OpenChatFixtureDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT icon_path FROM agents WHERE id = $agent;";
        command.Parameters.AddWithValue("$agent", ChatAgentId);
        var value = command.ExecuteScalar();
        return value is null or DBNull
            ? null
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? ReadAgentName()
    {
        using var connection = OpenChatFixtureDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM agents WHERE id = $agent;";
        command.Parameters.AddWithValue("$agent", ChatAgentId);
        var value = command.ExecuteScalar();
        return value is null or DBNull
            ? null
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqliteConnection OpenChatFixtureDatabase()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(ResolveDataRoot(), "connectonion.db"),
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        connection.Open();
        return connection;
    }
}
