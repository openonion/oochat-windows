using System.IO;

namespace ConnectOnion.WinUIClient.UnitTests.Architecture;

/// <summary>
/// The markdown renderer lives in the app project, which no headless test host can load, so these
/// are source contracts rather than render assertions. Each one pins a node type that
/// <c>UseAdvancedExtensions</c> turns on and that rendered wrongly — silently — until it was
/// looked at in a real window. The failure mode they share is that Markdig writes *nothing* for a
/// node with no registered renderer, and prints raw source for one rendered naively; neither
/// throws, so only looking at the output finds them.
/// </summary>
public sealed class MarkdownRendererCoverageTests
{
    [Fact]
    public void TaskListItems_HaveARendererAndACheckboxGlyph()
    {
        // Without a registered TaskList renderer, "- [x] done" and "- [ ] todo" both rendered as a
        // bare bullet — a plan's checklist lost every checkbox and the two states were identical.
        Assert.Contains("ObjectRenderers.Add(new TaskListRenderer())", Renderer);
        Assert.Contains("class TaskListRenderer", RendererDispatch);
        Assert.Contains("inline.Checked", Renderer);
        Assert.Contains("☑", Renderer);
        Assert.Contains("☐", Renderer);
    }

    [Fact]
    public void Strikethrough_DrawsALineRatherThanBorrowingTheSubscriptStyle()
    {
        // '~~' and '~' both reach WriteEmphasis. They must not share a presentation: crossing an
        // item out and stressing it are opposite meanings, and both used to render grey italic.
        Assert.Contains("DelimiterChar == '~' && inline.DelimiterCount >= 2", Renderer);
        Assert.Contains("TextDecorations = TextDecorations.Strikethrough", Renderer);
    }

    [Fact]
    public void InlineHtml_IsNotPrintedAsRawTags()
    {
        // WriteHtmlInline used to write inline.Tag straight out, so an agent's <b>text</b> showed
        // its angle brackets and a <br> in a table cell appeared as literal characters.
        Assert.DoesNotContain("WriteHighlightedText(inline.Tag)", Renderer);
        Assert.Contains("StartsWith(\"<br\"", Renderer);
    }

    [Fact]
    public void ImageReferences_ShowAltTextAndAreNotAutoFetched()
    {
        // Alt text, not the raw URL. And still no network fetch: images the agent actually sends
        // arrive as agent_image events through AttachmentImageCacheService, and following an
        // arbitrary URL out of agent markdown would be an outbound request nobody asked for.
        Assert.Contains("if (inline.IsImage)", Renderer);
        Assert.Contains("WriteImageReference(", Renderer);
        Assert.DoesNotContain("new BitmapImage", Renderer);
    }

    [Fact]
    public void ThematicBreak_IsAMeasuredRuleRatherThanFixedGlyphs()
    {
        // Ten literal U+2500 characters drew a stub that ignored the control's width.
        Assert.DoesNotContain("───", Renderer);
        Assert.Contains("_thematicBreaks", Renderer);
        Assert.Contains("OnTargetSizeChanged", Renderer);
        // Re-widening a rule that belongs to a discarded pass would write into a detached tree.
        Assert.Contains("_thematicBreaks.Clear()", Renderer);
    }

    [Fact]
    public void Tables_AdvanceColumnsAlignTextAndOwnHorizontalScrollingOnly()
    {
        Assert.Contains("ResolveCellColumnIndex(cell, nextColumnIndex)", Renderer);
        Assert.Contains("nextColumnIndex = columnIndex + columnSpan", Renderer);
        Assert.Contains("GetTableTextAlignment(table, columnIndex)", Renderer);
        Assert.Contains("MaxWidth = 320", Renderer);
        Assert.Contains("TextWrapping = TextWrapping.WrapWholeWords", Renderer);
        Assert.Contains("HorizontalScrollMode = ScrollMode.Auto", Renderer);
        Assert.Contains("VerticalScrollMode = ScrollMode.Disabled", Renderer);
        Assert.Contains("isHeader ? FontWeights.SemiBold", Renderer);
    }

    [Fact]
    public void FencedCodeLanguage_UsesACompactHeaderWithoutChangingPlainBlocks()
    {
        Assert.Contains("GetCodeLanguage(block)", Renderer);
        Assert.Contains("if (!string.IsNullOrEmpty(language))", Renderer);
        Assert.Contains("var languageHeader = new Border", Renderer);
        Assert.Contains("Text = language", Renderer);
        Assert.Contains("WriteCodeBlockLines(block.Lines.ToString())", Renderer);
    }

    private static string Renderer => ReadSource("Rendering", "WinUiMarkdownRenderer.cs");
    private static string RendererDispatch => ReadSource("Rendering", "WinUiMarkdownRenderer.Renderers.cs");

    private static string ReadSource(params string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                new[] { dir.FullName, "ConnectOnion.WinUIClient" }.Concat(relativePath).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(relativePath)} from {AppContext.BaseDirectory}");
    }
}
