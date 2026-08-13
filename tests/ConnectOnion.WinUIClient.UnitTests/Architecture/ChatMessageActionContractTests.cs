namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

public sealed class ChatMessageActionContractTests
{
    [Fact]
    public void MessageMetadataAndCommands_StayVisibleAndGainEmphasisOnHoverOrFocus()
    {
        var xaml = ReadRepositoryFile("ConnectOnion.WinUIClient", "Views", "ChatPage.xaml");
        var model = ReadRepositoryFile("ConnectOnion.WinUIClient.Core", "Models", "ChatMessage.cs");
        const string opacityBinding = "Opacity=\"{x:Bind UserActionsOpacity, Mode=OneWay}\"";

        Assert.Equal(2, xaml.Split(opacityBinding, StringSplitOptions.None).Length - 1);
        Assert.Contains("ShowUserActions ? 1d : 0.75d", model, StringComparison.Ordinal);
    }

    /// <summary>The resting action row is dimmed rather than hidden, so its opacity *is* the
    /// contrast ratio of an icon-only control — WCAG's 3:1 non-text minimum applies. A second
    /// literal <c>Opacity</c> anywhere inside the two action strips multiplies against
    /// <see cref="ChatMessage.UserActionsOpacity"/> and silently drops the result below it,
    /// which is exactly how it reached ~1.9:1.</summary>
    [Fact]
    public void ActionStrips_DoNotMultiplyASecondOpacityAgainstTheContrastFloor()
    {
        var xaml = ReadRepositoryFile("ConnectOnion.WinUIClient", "Views", "ChatPage.xaml");

        // Each strip is the StackPanel between the bound Grid and its first action Button.
        foreach (var strip in ActionStripBodies(xaml))
            Assert.DoesNotContain("Opacity=\"", strip, StringComparison.Ordinal);
    }

    /// <summary>Icon-only commands are pointer *and* touch targets; keep the full 40 epx touch
    /// target even though their visual glyph remains intentionally compact.</summary>
    [Fact]
    public void ActionButtons_MeetTheMinimumTargetSize()
    {
        var xaml = ReadRepositoryFile("ConnectOnion.WinUIClient", "Views", "ChatPage.xaml");

        foreach (var strip in ActionStripBodies(xaml))
        {
            Assert.DoesNotContain("MinWidth=\"28\"", strip, StringComparison.Ordinal);
            Assert.DoesNotContain("MinHeight=\"28\"", strip, StringComparison.Ordinal);
            Assert.Contains("MinWidth=\"40\"", strip, StringComparison.Ordinal);
            Assert.Contains("MinHeight=\"40\"", strip, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> ActionStripBodies(string xaml)
    {
        const string opacityBinding = "Opacity=\"{x:Bind UserActionsOpacity, Mode=OneWay}\"";
        var index = 0;
        while ((index = xaml.IndexOf(opacityBinding, index, StringComparison.Ordinal)) >= 0)
        {
            index += opacityBinding.Length;
            var end = xaml.IndexOf("</Grid>", index, StringComparison.Ordinal);
            Assert.True(end > index, "Action row Grid is not closed.");
            yield return xaml[index..end];
        }
    }

    [Fact]
    public void RetryStaysInCurrentSession_WhileEditBranches()
    {
        var actions = ReadRepositoryFile(
            "ConnectOnion.WinUIClient", "Views", "ChatPage.MessageActions.cs");
        var conversations = ReadRepositoryFile(
            "ConnectOnion.WinUIClient", "ViewModels", "ChatViewModel.Conversation.cs");

        Assert.Contains("Vm.RetryUserMessageAsync(message)", actions, StringComparison.Ordinal);
        Assert.Contains("Vm.RetryFromAgentMessageAsync(message)", actions, StringComparison.Ordinal);
        Assert.Contains("Vm.BranchFromMessageAsync(message, replacement)", actions, StringComparison.Ordinal);
        Assert.Contains("await SendAsync(source.Content);", conversations, StringComparison.Ordinal);
        Assert.Contains("return RetryUserMessageAsync(source);", conversations, StringComparison.Ordinal);
        Assert.Contains("_session = SessionSummary.NewConversation", conversations, StringComparison.Ordinal);
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
