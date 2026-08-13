using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ConnectOnion.WinUIClient.Rendering;

// The Markdig ObjectRenderer dispatch table for WinUiMarkdownRenderer: one tiny
// nested renderer per markdown node type, each forwarding to the matching
// WriteXxx method in WinUiMarkdownRenderer.cs (where the real rendering lives).
// Registered in the WinUiMarkdownRenderer constructor. Split out so the main
// file holds only the rendering logic, not the boilerplate visitor plumbing.
public sealed partial class WinUiMarkdownRenderer
{
    private sealed class DocumentRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, MarkdownDocument>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, MarkdownDocument obj)
            => renderer.WriteDocument(obj);
    }

    private sealed class HeadingRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, HeadingBlock>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, HeadingBlock obj)
            => renderer.WriteHeadingBlock(obj);
    }

    private sealed class ParagraphRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, ParagraphBlock>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, ParagraphBlock obj)
            => renderer.WriteParagraphBlock(obj);
    }

    private sealed class QuoteBlockRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, QuoteBlock>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, QuoteBlock obj)
            => renderer.WriteQuoteBlock(obj);
    }

    private sealed class ListBlockRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, ListBlock>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, ListBlock obj)
            => renderer.WriteListBlock(obj);
    }

    private sealed class FencedCodeBlockRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, FencedCodeBlock>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, FencedCodeBlock obj)
            => renderer.WriteFencedCodeBlock(obj);
    }

    private sealed class CodeBlockRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, CodeBlock>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, CodeBlock obj)
            => renderer.WriteCodeBlock(obj);
    }

    private sealed class ThematicBreakRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, ThematicBreakBlock>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, ThematicBreakBlock obj)
            => renderer.WriteThematicBreak();
    }

    private sealed class TableBlockRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, Table>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, Table obj)
            => renderer.WriteTable(obj);
    }

    private sealed class HtmlBlockRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, HtmlBlock>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, HtmlBlock obj)
            => renderer.WriteHtmlBlock(obj);
    }

    private sealed class LiteralInlineRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, LiteralInline>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, LiteralInline obj)
            => renderer.WriteLiteral(obj);
    }

    private sealed class LineBreakInlineRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, LineBreakInline>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, LineBreakInline obj)
            => renderer.WriteLineBreak(obj);
    }

    private sealed class TaskListRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, TaskList>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, TaskList obj)
            => renderer.WriteTaskList(obj);
    }

    private sealed class CodeInlineRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, CodeInline>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, CodeInline obj)
            => renderer.WriteCodeInline(obj);
    }

    private sealed class EmphasisInlineRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, EmphasisInline>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, EmphasisInline obj)
            => renderer.WriteEmphasis(obj);
    }

    private sealed class LinkInlineRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, LinkInline>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, LinkInline obj)
            => renderer.WriteLink(obj);
    }

    private sealed class ContainerInlineRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, ContainerInline>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, ContainerInline obj)
            => renderer.WriteContainerInline(obj);
    }

    private sealed class HtmlInlineRenderer : MarkdownObjectRenderer<WinUiMarkdownRenderer, HtmlInline>
    {
        protected override void Write(WinUiMarkdownRenderer renderer, HtmlInline obj)
            => renderer.WriteHtmlInline(obj);
    }
}
