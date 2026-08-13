using System.Xml.Linq;

namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class StopUiContractTests
{
    [Fact]
    public void ApprovalSurface_ExposesFiveProtocolActionsWithoutAFeedbackInput()
    {
        // The decision surface moved out of ToolActivityView into its own card. That card is a
        // turn-level aggregate anchored at the turn's FIRST tool call, while an approval arrives
        // much later — so a turn that had also appended a plan, a question or a diff drew the live
        // decision back up above all of them, mid-conversation.
        var xaml = ReadRepositoryFile(
            "ConnectOnion.WinUIClient", "Controls", "Chat", "InteractiveCards", "ApprovalCard.xaml");
        var source = ReadRepositoryFile(
            "ConnectOnion.WinUIClient", "Controls", "Chat", "InteractiveCards", "ApprovalCard.xaml.cs");

        Assert.Contains("AllowOnceCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("TrustSessionCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("RejectCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("StopCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ExplainCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Explain why", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TextBox Header=\"Reason for declining", xaml, StringComparison.Ordinal);

        // The command disclosure is useful only when MaxLines actually trimmed rendered text.
        // Character-count heuristics cannot account for card width, DPI, or font metrics and left
        // a no-op "Show full command" button beside already-complete one-line commands.
        var approvalDocument = XDocument.Parse(xaml);
        var commandText = approvalDocument.Descendants().Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "ApprovalCommandTextBlock"));
        var commandToggle = approvalDocument.Descendants().Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "ApprovalCommandToggleButton"));
        Assert.Equal(
            "ApprovalCommandText_IsTextTrimmedChanged",
            commandText.Attribute("IsTextTrimmedChanged")?.Value);
        Assert.Equal("Collapsed", commandToggle.Attribute("Visibility")?.Value);
        Assert.DoesNotContain("ShowApprovalCommandToggle", xaml, StringComparison.Ordinal);
        Assert.Contains("sender.IsTextTrimmed", source, StringComparison.Ordinal);
        Assert.Contains("Message?.IsApprovalCommandExpanded == true", source, StringComparison.Ordinal);

        // And it is genuinely gone from the tool card rather than duplicated into both.
        var toolCard = ReadRepositoryFile(
            "ConnectOnion.WinUIClient", "Controls", "Chat", "ToolActivityView.xaml");
        Assert.DoesNotContain("AllowOnceCommand", toolCard, StringComparison.Ordinal);
        Assert.DoesNotContain("ApprovalSection", toolCard, StringComparison.Ordinal);
        // The back-reference stays: it is what puts "Approval required" on the tool card's header
        // and folds its timeline while the decision is open.
        Assert.Contains("Activity.IsAwaitingApproval", toolCard, StringComparison.Ordinal);
    }

    [Fact]
    public void Composer_StopIsImmediate_AndRunningTurnKeepsInputAvailable()
    {
        var source = ReadRepositoryFile("ConnectOnion.WinUIClient", "Controls", "Chat", "ChatComposer.xaml.cs");
        var xaml = ReadRepositoryFile("ConnectOnion.WinUIClient", "Controls", "Chat", "ChatComposer.xaml");
        var page = ReadRepositoryFile("ConnectOnion.WinUIClient", "Views", "ChatPage.xaml");

        Assert.Contains("StopButton.IsEnabled = CanStop", source, StringComparison.Ordinal);
        Assert.Contains("InputBox.IsEnabled = CanSubmit", source, StringComparison.Ordinal);
        Assert.Contains(
            "var canSend = CanSubmit && !IsSubmitBlocked && !isAttachmentsBusy",
            source, StringComparison.Ordinal);

        // IsSubmitBlocked is "you may write, you just cannot send it" — the state while the agent
        // is parked on an interactive card. It must reach the send button and nothing else. The
        // three composition controls stay on CanSubmit alone, because disabling a focused TextBox
        // makes WinUI move focus to the next enabled sibling — the approval-mode button — so
        // routing this through CanSubmit drew a focus ring around "Safe" every time an approval
        // arrived while the caret was in the box.
        foreach (var line in source.Split('\n').Where(candidate => candidate.Contains(
                     "IsSubmitBlocked", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("InputBox.IsEnabled", line, StringComparison.Ordinal);
            Assert.DoesNotContain("AddAttachmentButton.IsEnabled", line, StringComparison.Ordinal);
            Assert.DoesNotContain("SpeechButton.IsEnabled", line, StringComparison.Ordinal);
        }
        Assert.Contains("AddAttachmentButton.IsEnabled = CanSubmit && !isActive", source, StringComparison.Ordinal);
        Assert.Contains("SpeechButton.IsEnabled = CanSubmit && !isActive", source, StringComparison.Ordinal);
        Assert.Contains(
            "IsSubmitBlocked=\"{x:Bind Vm.IsAwaitingUserDecision", page, StringComparison.Ordinal);
        Assert.Contains("RuntimeComposerStopping", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StopRing\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsBusy=\"{x:Bind Vm.IsComposerBusy", page, StringComparison.Ordinal);
        Assert.Contains("IsStopping=\"{x:Bind Vm.IsStopping", page, StringComparison.Ordinal);
        Assert.Contains("MinimumStopFeedbackDuration = TimeSpan.FromMilliseconds(500)", source, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(MinimumStopFeedbackDuration, _lifetimeCts.Token)", source, StringComparison.Ordinal);
        Assert.Contains("var showStop = CanStop || IsStopping || _showStopFeedback", source, StringComparison.Ordinal);
        Assert.Contains("StopButton.Visibility = showStop", source, StringComparison.Ordinal);
        Assert.Contains("StopButton.IsEnabled = CanStop && !IsStopping && !_showStopFeedback", source, StringComparison.Ordinal);
        Assert.Contains("var animateStop = CanStop && !IsStopping && !_showStopFeedback", source, StringComparison.Ordinal);
        Assert.Contains("StopRing.IsActive = animateStop", source, StringComparison.Ordinal);
        Assert.Contains("StopGlyph.Visibility = showStop", source, StringComparison.Ordinal);

        var stopButton = XDocument.Parse(xaml).Descendants().Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "StopButton"));
        Assert.Equal("40", stopButton.Attribute("Width")?.Value);
        Assert.Equal("40", stopButton.Attribute("Height")?.Value);
        Assert.Equal("20", stopButton.Attribute("CornerRadius")?.Value);
        Assert.DoesNotContain("StopButton.Width", source, StringComparison.Ordinal);

        var stopStateBrushes = stopButton.Descendants()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .ToArray();
        Assert.Contains(stopStateBrushes, element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key"
                && attribute.Value == "ButtonBackgroundPointerOver")
            && element.Attribute("Color")?.Value.Contains("BrandPrimaryHoverColor", StringComparison.Ordinal) == true);
        Assert.Contains(stopStateBrushes, element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key"
                && attribute.Value == "ButtonBackgroundPressed")
            && element.Attribute("Color")?.Value.Contains("BrandPrimaryPressedColor", StringComparison.Ordinal) == true);
        Assert.Contains(stopStateBrushes, element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key"
                && attribute.Value == "ButtonBackgroundDisabled")
            && element.Attribute("Color")?.Value.Contains("BrandPrimaryColor", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void GlobalStop_AlwaysUsesInterrupt_AndRuntimeInputReusesTheActiveSocket()
    {
        var manager = ReadRepositoryFile(
            "ConnectOnion.WinUIClient.Core", "Services", "Runtime", "AgentSessionManager.cs");
        var viewModel = ReadRepositoryFile(
            "ConnectOnion.WinUIClient", "ViewModels", "ChatViewModel.cs");
        var runViewModel = ReadRepositoryFile(
            "ConnectOnion.WinUIClient", "ViewModels", "ChatViewModel.Run.cs");

        Assert.Contains("await connection.SendInterruptAsync()", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("RejectApprovalForStopAsync", manager, StringComparison.Ordinal);
        Assert.Contains("await connection.SendRuntimeInputAsync(", manager, StringComparison.Ordinal);
        Assert.Contains("var isRuntimeInput = IsProcessing;", runViewModel, StringComparison.Ordinal);
        Assert.Contains("public bool CanSend => HasAgent && _session is not null && IsOnline && !IsConnecting",
            viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void StopButton_IsReachableWhileAQueuedSendCanStillBeCancelled()
    {
        var source = ReadRepositoryFile(
            "ConnectOnion.WinUIClient", "ViewModels", "ChatViewModel.Run.cs");

        Assert.Contains(
            "snapshot.Status is ConversationRunStatus.Queued or ConversationRunStatus.Connecting",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "snapshot.Status == ConversationRunStatus.Running || CanCancelSend",
            source,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] relativeParts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            var candidate = Path.Combine([root.FullName, .. relativeParts]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            root = root.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, relativeParts));
    }
}
