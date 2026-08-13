using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class DiffPreviewModelTests
{
    [Fact]
    public void NewEmptyFile_WithOneAddedLine_CountsOneAddition()
    {
        var model = DiffPreviewModel.Parse("/tmp/hello.txt", "+ hello world", isNewFile: true);

        Assert.True(model.IsUnifiedDiff);
        Assert.Equal(1, model.Additions);
        Assert.Equal(0, model.Deletions);
        Assert.Equal("+1  −0", model.ChangeSummary);
        Assert.Equal(DiffLineKind.Addition, Assert.Single(model.Files[0].Lines).Kind);
    }

    [Fact]
    public void MultiLineDiff_CountsAdditionsAndDeletionsIncludingUnicodeAndTrailingNewline()
    {
        const string diff = "@@ -1,3 +1,4 @@\n-old\n-旧值\n+new\n+新值\n+last\n context\n";
        var model = DiffPreviewModel.Parse("unicode.txt", diff);

        Assert.Equal(3, model.Additions);
        Assert.Equal(2, model.Deletions);
        Assert.Equal(3, model.Files[0].Additions);
        Assert.Equal(2, model.Files[0].Deletions);
    }

    [Fact]
    public void EmptyAndDeletedFiles_HaveCorrectTotals()
    {
        var empty = DiffPreviewModel.Parse("empty.txt", "", isNewFile: true);
        var deleted = DiffPreviewModel.Parse("gone.txt", "@@ -1 +0,0 @@\n-gone");

        Assert.Equal(0, empty.Additions);
        Assert.Equal(0, empty.Deletions);
        Assert.Equal(0, deleted.Additions);
        Assert.Equal(1, deleted.Deletions);
    }

    [Fact]
    public void SingleFile_ParsesLineNumbersAndChangeCounts()
    {
        var model = DiffPreviewModel.Parse("config.yml", "@@ -18,2 +18,3 @@\n server:\n-  port: 8000\n+  port: 8080\n+  logging: true");
        var file = Assert.Single(model.Files);
        Assert.True(model.IsUnifiedDiff);
        Assert.Equal(2, file.Additions);
        Assert.Equal(1, file.Deletions);
        Assert.Contains(file.Lines, line => line.Kind == DiffLineKind.Deletion && line.OldLineNumber == 19);
        Assert.Contains(file.Lines, line => line.Kind == DiffLineKind.Addition && line.NewLineNumber == 20);
    }

    [Fact]
    public void UnifiedGutter_UsesOneAlignedDisplayNumberPerChangedRow()
    {
        var model = DiffPreviewModel.Parse("test.txt", "@@ -1 +1 @@\n-old\n+new");
        var changed = model.Files[0].Lines
            .Where(line => line.Kind is DiffLineKind.Deletion or DiffLineKind.Addition)
            .ToArray();

        Assert.Equal([1, 1], changed.Select(line => line.DisplayLineNumber));
        Assert.Equal(1, changed[0].OldLineNumber);
        Assert.Equal(1, changed[1].NewLineNumber);
    }

    [Fact]
    public void JoinedFinalReplacement_IsRepairedUsingHunkCounts()
    {
        const string diff = "--- a/test1.txt\n+++ b/test1.txt\n@@ -1 +1 @@\n- hello world+new";

        var model = DiffPreviewModel.Parse("/home/user/test1.txt", diff);
        var changedLines = model.Files[0].Lines
            .Where(line => line.Kind is DiffLineKind.Deletion or DiffLineKind.Addition)
            .ToList();

        Assert.Equal(1, model.Deletions);
        Assert.Equal(1, model.Additions);
        Assert.Collection(changedLines,
            line =>
            {
                Assert.Equal(DiffLineKind.Deletion, line.Kind);
                Assert.Equal(" hello world", line.Content);
            },
            line =>
            {
                Assert.Equal(DiffLineKind.Addition, line.Kind);
                Assert.Equal("new", line.Content);
            });
        Assert.Equal(diff, model.RawText);
    }

    [Fact]
    public void PlusInsideDeletion_IsNotSplitWhenHunkCountsAreAlreadySatisfied()
    {
        const string diff = "@@ -1 +1 @@\n-a+b\n+new";

        var model = DiffPreviewModel.Parse("math.txt", diff);

        Assert.Equal(1, model.Deletions);
        Assert.Equal(1, model.Additions);
        Assert.Contains(model.Files[0].Lines,
            line => line.Kind == DiffLineKind.Deletion && line.Content == "a+b");
    }

    [Fact]
    public void MultipleFiles_AreGroupedAndOnlyFirstStartsExpanded()
    {
        const string diff = "diff --git a/a.txt b/a.txt\n@@ -1 +1 @@\n-old\n+new\ndiff --git a/b.txt b/b.txt\n@@ -2 +2 @@\n-x\n+y";
        var model = DiffPreviewModel.Parse("fallback.txt", diff);
        Assert.Equal(2, model.Files.Count);
        Assert.Equal("a.txt", model.Files[0].Path);
        Assert.Equal("b.txt", model.Files[1].Path);
        Assert.True(model.Files[0].IsExpanded);
        Assert.False(model.Files[1].IsExpanded);
    }

    [Fact]
    public void MarkdownOrPlainText_IsNotMisclassifiedAsUnifiedDiff()
    {
        var model = DiffPreviewModel.Parse("notes.md", "+ bullet\n- another bullet\n# Heading");
        Assert.False(model.IsUnifiedDiff);
        Assert.Equal(0, model.Additions);
        Assert.Equal(0, model.Deletions);
        Assert.All(model.Files[0].Lines, line => Assert.Equal(DiffLineKind.Context, line.Kind));
    }

    [Fact]
    public void RawCopyText_DoesNotContainVisualLineNumbers()
    {
        const string raw = "@@ -8 +8 @@\n-old\n+new";
        var model = DiffPreviewModel.Parse("a.txt", raw);
        Assert.Equal(raw, model.RawText);
        Assert.DoesNotContain("8 |", model.RawText);
    }

    [Fact]
    public void LargeDiff_BoundsPreviewModelsButKeepsRawCopyAndTotals()
    {
        var lines = Enumerable.Range(0, DiffPreviewModel.MaxPreviewLines + 25)
            .Select(index => $"+line {index}");
        var raw = string.Join('\n', lines);

        var model = DiffPreviewModel.Parse("large.txt", raw, isNewFile: true);

        Assert.Equal(DiffPreviewModel.MaxPreviewLines, model.Files.Sum(file => file.Lines.Count));
        Assert.Equal(25, model.OmittedLineCount);
        Assert.True(model.IsPreviewTruncated);
        Assert.Equal(DiffPreviewModel.MaxPreviewLines + 25, model.Additions);
        Assert.Equal(raw, model.RawText);
    }

    [Fact]
    public void OpenDiff_UsesBoundedViewportRegardlessOfBusinessState()
    {
        var message = new ChatMessage { EventKind = "diff_preview" };
        Assert.Equal(360, message.DiffViewportHeight);
        message.SetDiffState(DiffChangeState.Applied);
        Assert.Equal(360, message.DiffViewportHeight);
    }

    [Fact]
    public void DiffActions_AreOnlyOfferedWhenTheyChangeThePresentation()
    {
        var shortDiff = new ChatMessage
        {
            EventKind = "diff_preview",
            EventTitle = "a.txt",
            EventDetail = "+hello",
        };
        var longDiff = new ChatMessage
        {
            EventKind = "diff_preview",
            EventTitle = "a.txt",
            EventDetail = string.Join('\n', Enumerable.Repeat("+" + new string('x', 120), 14)),
        };

        Assert.False(shortDiff.CanExpandDiff);
        Assert.False(shortDiff.CanWrapDiff);
        Assert.True(longDiff.CanExpandDiff);
        Assert.True(longDiff.CanWrapDiff);
    }

    [Fact]
    public void NewFileProjectionFlag_DrivesMarkerOnlyPreviewParsing()
    {
        var message = new ChatMessage
        {
            EventKind = "diff_preview",
            EventTitle = "/home/user/test.txt",
            EventEyebrow = "CREATE",
            EventDetail = "+hello world",
        };

        Assert.Equal(1, message.DiffPreview.Additions);
        Assert.Equal("test.txt", message.DiffPreview.FileSummary);
        Assert.Equal("/home/user/test.txt", message.DiffContextPath);
    }

    [Fact]
    public void DiffFoldsThroughItsStates_AndTheUserCanAlwaysOverrideIt()
    {
        var message = DiffMessage();
        message.SetDiffState(DiffChangeState.Pending);

        // Pending opens for review, and — unlike before — can be folded while it waits.
        Assert.True(message.IsDiffExpanded);
        Assert.True(message.CanToggleDiffVisibility);

        // Applying folds: transient, nothing to decide, nothing yet to inspect.
        message.SetDiffState(DiffChangeState.Applying);
        Assert.False(message.IsDiffExpanded);

        message.SetDiffState(DiffChangeState.Applied);
        Assert.False(message.IsDiffExpanded);
        Assert.Equal("Changes applied to test.txt", message.DiffCardTitle);

        message.ToggleDiffExpanded();
        Assert.True(message.IsDiffExpanded);
        Assert.Equal("Hide changes", message.DiffToggleLabel);
    }

    /// <summary>A diff moves through up to three states in one turn, so state-driven default
    /// expansion gets several chances to overrule the user. One manual toggle ends that for the
    /// life of the card — same contract as <c>ToolActivityViewModel.HasUserExpansionOverride</c>.</summary>
    [Fact]
    public void ManualFold_IsNotUndoneByLaterStateTransitions()
    {
        var message = DiffMessage();
        message.SetDiffState(DiffChangeState.Pending);
        message.ToggleDiffExpanded();
        Assert.False(message.IsDiffExpanded);

        message.SetDiffState(DiffChangeState.Applying);
        message.SetDiffState(DiffChangeState.Failed);

        // Failed would otherwise re-open the card; the user's choice outranks it.
        Assert.False(message.IsDiffExpanded);
    }

    [Fact]
    public void TogglingDiffOnlyChangesDisclosureState()
    {
        var message = DiffMessage();
        message.SetDiffState(DiffChangeState.Applied);
        var title = message.DiffCardTitle;
        var subtitle = message.DiffCardSubtitle;
        var headerMeta = message.DiffHeaderMeta;
        var icon = message.DiffIconGlyph;
        var iconTone = message.DiffIconTone;
        var chromeTone = message.DiffChromeTone;

        message.ToggleDiffExpanded();

        Assert.True(message.ShowDiffBody);
        Assert.Equal("Hide changes", message.DiffToggleLabel);
        Assert.Equal(title, message.DiffCardTitle);
        Assert.Equal(subtitle, message.DiffCardSubtitle);
        Assert.Equal(headerMeta, message.DiffHeaderMeta);
        Assert.Equal(icon, message.DiffIconGlyph);
        Assert.Equal(iconTone, message.DiffIconTone);
        Assert.Equal(chromeTone, message.DiffChromeTone);
    }

    [Fact]
    public void RejectedFoldsWithoutClaimingTheFileChanged()
    {
        var message = DiffMessage();
        message.SetDiffState(DiffChangeState.Pending);
        message.ApplyDiffApprovalAnswer("Answered: No, reject and give feedback");

        Assert.Equal(DiffChangeState.Rejected, message.DiffState);
        Assert.False(message.IsDiffExpanded);
        Assert.Equal("Proposed changes rejected", message.DiffCardTitle);
        Assert.Equal("View proposed changes", message.DiffToggleLabel);
        Assert.DoesNotContain("applied", message.DiffCardTitle, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DiffChangeState.Failed)]
    [InlineData(DiffChangeState.PartiallyApplied)]
    [InlineData(DiffChangeState.Disconnected)]
    [InlineData(DiffChangeState.Unconfirmed)]
    public void UncertainOutcomesRemainExpanded(DiffChangeState state)
    {
        var message = DiffMessage();
        message.SetDiffState(DiffChangeState.Applied);
        message.SetDiffState(state);

        Assert.True(message.IsDiffExpanded);
        Assert.True(message.ShowDiffProblem);
        Assert.DoesNotContain("Changes applied", message.DiffCardTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoricalAppliedAndRejectedRestoreFolded_WhilePendingAndFailedRestoreExpanded()
    {
        Assert.False(Restored(DiffChangeState.Applied).IsDiffExpanded);
        Assert.False(Restored(DiffChangeState.Rejected).IsDiffExpanded);
        Assert.True(Restored(DiffChangeState.Pending).IsDiffExpanded);
        Assert.True(Restored(DiffChangeState.Failed).IsDiffExpanded);
    }

    [Fact]
    public void UnrelatedPropertyChangesDoNotResetUserExpansionChoice()
    {
        var message = Restored(DiffChangeState.Applied);
        message.ToggleDiffExpanded();
        Assert.True(message.IsDiffExpanded);

        message.EventMeta = "unrelated metadata";
        message.Status = EventStatus.Done;

        Assert.True(message.IsDiffExpanded);
    }

    private static ChatMessage DiffMessage() => new()
    {
        EventKind = "diff_preview",
        EventTitle = "/tmp/test.txt",
        EventEyebrow = "CREATE",
        EventDetail = "+hello world",
    };

    private static ChatMessage Restored(DiffChangeState state) => new()
    {
        EventKind = "diff_preview",
        EventTitle = "/tmp/test.txt",
        EventEyebrow = "CREATE",
        EventDetail = "+hello world",
        EventResult = $"diff-state:{state}",
        Status = EventStatus.Done,
    };
}
