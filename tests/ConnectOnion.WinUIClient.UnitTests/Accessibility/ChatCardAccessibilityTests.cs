namespace ConnectOnion.WinUIClient.UnitTests.Accessibility;

public sealed class ChatCardAccessibilityTests
{
    [Fact]
    public void AttachmentOpenSurfaces_AreKeyboardInvokableButtons()
    {
        var chat = ReadApp("Views", "ChatPage.xaml");
        var ask = ReadApp("Controls", "Chat", "InteractiveCards", "AskUserCard.xaml");

        Assert.Contains("Click=\"AttachmentOpen_Click\"", chat, StringComparison.Ordinal);
        Assert.DoesNotContain("AttachmentImage_Tapped", chat, StringComparison.Ordinal);
        Assert.DoesNotContain("AttachmentFile_Tapped", chat, StringComparison.Ordinal);
        Assert.Contains("Click=\"QuestionImage_Click\"", ask, StringComparison.Ordinal);
        Assert.DoesNotContain("QuestionImage_Tapped", ask, StringComparison.Ordinal);
    }

    [Fact]
    public void AskUserOptions_ExposeNativeSelectionPatterns()
    {
        var ask = ReadApp("Controls", "Chat", "InteractiveCards", "AskUserCard.xaml");

        Assert.Contains("<RadioButton", ask, StringComparison.Ordinal);
        Assert.Contains("<CheckBox", ask, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"Option_Click\"", ask, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanReview_UsesOuterTranscriptScrollAndResponsiveDialogBounds()
    {
        var card = ReadApp("Controls", "Chat", "InteractiveCards", "PlanReviewCard.xaml");
        var dialog = ReadApp("Controls", "Chat", "InteractiveCards", "PlanSectionReviewDialog.xaml");
        var dialogCode = ReadApp("Controls", "Chat", "InteractiveCards", "PlanSectionReviewDialog.xaml.cs");

        Assert.DoesNotContain("<ScrollViewer", card, StringComparison.Ordinal);
        Assert.DoesNotContain("Width=\"620\"", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"620\"", dialog, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(root.Size.Width", dialogCode, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(root.Size.Height", dialogCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownCodeBlocks_KeepTextClearOfTheOverlayScrollBar()
    {
        var renderer = ReadApp("Rendering", "WinUiMarkdownRenderer.cs");

        Assert.Contains("CodeBlockScrollBarClearance = 8", renderer, StringComparison.Ordinal);
        Assert.Contains(
            "CodeBlockVerticalPadding + CodeBlockScrollBarClearance",
            renderer,
            StringComparison.Ordinal);
    }

    private static string ReadApp(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var root = Path.Combine(directory.FullName, "ConnectOnion.WinUIClient");
            if (Directory.Exists(root))
                return File.ReadAllText(Path.Combine([root, .. relativeParts]));
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the WinUI app source directory.");
    }
}
