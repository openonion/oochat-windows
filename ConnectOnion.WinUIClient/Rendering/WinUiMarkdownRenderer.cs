using System;
using System.Collections.Generic;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Text;
using ConnectOnion.WinUIClient.Controls;
using ConnectOnion.WinUIClient.Services;

namespace ConnectOnion.WinUIClient.Rendering;

/// <summary>
/// A small Markdig renderer that maps common markdown nodes to WinUI document elements.
/// </summary>
public sealed partial class WinUiMarkdownRenderer : RendererBase
{
    private const double CodeBlockVerticalPadding = 8;
    private const double CodeBlockScrollBarClearance = 8;

    /// <summary>The monospace face used for inline code, fenced blocks, and any search highlight
    /// inside them. One instance: it is immutable, and the descent cache in
    /// <see cref="HighlightedTextBlock"/> keys off its <c>Source</c>.</summary>
    private static readonly FontFamily CodeFont =
        new("ms-appx:///Assets/Fonts/JetBrainsMonoNerdFontMono-Regular.ttf#JetBrainsMono NFM");

    private readonly RichTextBlock _target;
    private readonly Stack<InlineCollection> _inlineScopes = new();
    // Not readonly: Reset() re-arms them so the renderer can be reused across renders.
    private string _searchQuery;
    private int _activeMatchIndex;
    private int _matchIndex;
    private int _quoteDepth;
    private int _listDepth;

    /// <summary>Greater than zero while the current inline scope sits inside an element that
    /// cannot host an <see cref="InlineUIContainer"/> — today that means a <see cref="Hyperlink"/>,
    /// which (unlike a Paragraph of the RichTextBlock) accepts only text inlines and throws
    /// <c>ArgumentException</c> on anything else. It is a depth, not a flag, because everything
    /// nested further inside — emphasis spans, inline code, search highlights — is blocked too.</summary>
    private int _uiContainerBlockedDepth;

    private bool CanHostUIContainer => _uiContainerBlockedDepth == 0;

    public WinUiMarkdownRenderer(RichTextBlock target, string searchQuery = "", int activeMatchIndex = -1)
    {
        _target = target;
        _searchQuery = searchQuery;
        _activeMatchIndex = activeMatchIndex;

        // Subscribed once, in the constructor, rather than per thematic break: this renderer is
        // owned by one control for that control's lifetime (see Reset), and _target is the same
        // RichTextBlock throughout, so one handler covers every render. Per-rule subscriptions
        // would accumulate on every recycle of a virtualized row.
        _target.SizeChanged += OnTargetSizeChanged;

        ObjectRenderers.Add(new DocumentRenderer());
        ObjectRenderers.Add(new HeadingRenderer());
        ObjectRenderers.Add(new ParagraphRenderer());
        ObjectRenderers.Add(new QuoteBlockRenderer());
        ObjectRenderers.Add(new ListBlockRenderer());
        ObjectRenderers.Add(new FencedCodeBlockRenderer());
        ObjectRenderers.Add(new CodeBlockRenderer());
        ObjectRenderers.Add(new ThematicBreakRenderer());
        ObjectRenderers.Add(new TableBlockRenderer());
        ObjectRenderers.Add(new HtmlBlockRenderer());

        ObjectRenderers.Add(new LiteralInlineRenderer());
        ObjectRenderers.Add(new LineBreakInlineRenderer());
        // Registered before CodeInline/Emphasis for no functional reason, but it must be registered
        // at all: TaskList is a LeafInline that UseAdvancedExtensions turns on, and Markdig
        // silently writes nothing for an inline with no renderer. Without this a "- [x] done" and a
        // "- [ ] todo" both rendered as a bare bullet — the checklist an agent writes into a plan
        // lost the one thing that made it a checklist.
        ObjectRenderers.Add(new TaskListRenderer());
        ObjectRenderers.Add(new CodeInlineRenderer());
        ObjectRenderers.Add(new EmphasisInlineRenderer());
        ObjectRenderers.Add(new LinkInlineRenderer());
        ObjectRenderers.Add(new ContainerInlineRenderer());
        ObjectRenderers.Add(new HtmlInlineRenderer());
    }

    /// <summary>Re-arms this renderer for another pass over the same target.
    ///
    /// <para>Exists so a <see cref="Controls.MarkdownTextBlock"/> can keep one renderer for its
    /// lifetime instead of constructing one per render. The constructor registers seventeen
    /// <c>ObjectRenderer</c> instances and runs the Markdig pipeline setup, and a virtualized
    /// transcript re-renders a row every time its container is recycled — so that was eighteen
    /// allocations and a pipeline walk per recycled row, to produce a renderer identical to the
    /// one just discarded.</para>
    ///
    /// <para>Only the three fields that vary are reset. <c>_target</c> does not: it is the
    /// control's own <c>RichTextBlock</c>, created once and never replaced, which is precisely
    /// what makes reuse safe here and would not be safe for a renderer shared across
    /// controls.</para></summary>
    internal void Reset(string searchQuery, int activeMatchIndex)
    {
        _searchQuery = searchQuery;
        _activeMatchIndex = activeMatchIndex;

        // _matchIndex is the running count of search hits emitted during a pass, and it is what
        // decides which one gets the "current match" styling. Carrying it into the next render
        // would put that highlight on the wrong occurrence — the single thing reuse would
        // silently break, so it is reset first.
        _matchIndex = 0;

        // The rest are balanced by construction and should already be at zero/empty. Reset
        // anyway: a render that threw part-way through would otherwise leave the next one
        // indented, quoted, or refusing UI containers for no visible reason.
        _uiContainerBlockedDepth = 0;
        _quoteDepth = 0;
        _listDepth = 0;
        _inlineScopes.Clear();

        // The previous pass's rules belong to blocks the caller is about to clear. Holding them
        // would both leak detached Borders and let a resize write widths into a dead tree.
        _thematicBreaks.Clear();
    }

    public override object Render(MarkdownObject markdownObject)
    {
        Write(markdownObject);
        return _target;
    }

    internal void WriteDocument(MarkdownDocument document)
    {
        foreach (var block in document)
        {
            Write(block);
        }
    }

    internal void WriteParagraphBlock(ParagraphBlock block, string? prefix = null)
    {
        var paragraph = CreateParagraph();

        if (!string.IsNullOrEmpty(prefix))
        {
            // A hanging indent keeps wrapped list text aligned with the content instead of the
            // bullet/number. Nested lists get one additional Fluent spacing unit per level.
            var leftIndent = 18 + (Math.Max(0, _listDepth - 1) * 18);
            paragraph.Margin = new Thickness(leftIndent, 2, 0, 2);
            paragraph.TextIndent = -18;
        }

        _target.Blocks.Add(paragraph);

        if (!string.IsNullOrEmpty(prefix))
        {
            paragraph.Inlines.Add(new Run { Text = prefix });
        }

        if (block.Inline is not null)
        {
            WriteInlineContainer(block.Inline, paragraph.Inlines);
        }
    }

    internal void WriteHeadingBlock(HeadingBlock block)
    {
        var paragraph = CreateParagraph();
        paragraph.FontWeight = FontWeights.SemiBold;
        // Heading sizes scale with the message's base font size (the "Message text size" setting)
        // rather than being fixed, so a heading stays proportionally larger than its body text at
        // every step. 14 is the Medium baseline the raw sizes below were picked against.
        var scale = _target.FontSize > 0 ? _target.FontSize / 14.0 : 1.0;
        // Parenthesized on purpose: `switch` binds looser than `*`, so without these parens this
        // reads as `(scale * block.Level) switch …` — matching a scaled level against 1/2/3/4.
        paragraph.FontSize = scale * (block.Level switch
        {
            1 => 24,
            2 => 20,
            3 => 18,
            4 => 16,
            _ => 14,
        });
        paragraph.Margin = new Thickness(0, block.Level <= 2 ? 10 : 8, 0, 6);
        _target.Blocks.Add(paragraph);

        if (block.Inline is not null)
        {
            WriteInlineContainer(block.Inline, paragraph.Inlines);
        }
    }

    internal void WriteQuoteBlock(QuoteBlock block)
    {
        _quoteDepth++;
        try
        {
            foreach (var child in block)
            {
                Write(child);
            }
        }
        finally
        {
            _quoteDepth--;
        }
    }

    internal void WriteListBlock(ListBlock block)
    {
        _listDepth++;
        try
        {
            var itemIndex = 1;
            foreach (var child in block)
            {
                if (child is not ListItemBlock item)
                {
                    continue;
                }

                var prefix = block.IsOrdered
                    ? $"{itemIndex}. "
                    : "\u2022 ";
                itemIndex++;

                WriteListItem(item, prefix);
            }
        }
        finally
        {
            _listDepth--;
        }
    }

    internal void WriteFencedCodeBlock(FencedCodeBlock block)
        => WriteCodeBlockLines(block.Lines.ToString(), GetCodeLanguage(block));

    internal void WriteCodeBlock(CodeBlock block)
        => WriteCodeBlockLines(block.Lines.ToString());

    /// <summary>
    /// A markdown <c>---</c> is a horizontal rule, so it has to span the content. This used to be
    /// ten literal U+2500 box-drawing characters, which drew a short stub hanging off the left
    /// margin at whatever width ten glyphs happened to be \u2014 it read as a rendering artifact rather
    /// than a divider, and it did not change with the control's width.
    ///
    /// <para>A <see cref="RichTextBlock"/> takes only <see cref="Paragraph"/> blocks (WinUI 3 has
    /// no <c>BlockUIContainer</c>), and an <see cref="InlineUIContainer"/> measures its child with
    /// infinite width \u2014 so a <c>Stretch</c> alignment does nothing here and the width has to be
    /// given explicitly. <see cref="_thematicBreaks"/> plus the one <c>SizeChanged</c> handler
    /// below is what keeps it right after a resize, a zoom change, or a sidebar drag.</para>
    /// </summary>
    internal void WriteThematicBreak()
    {
        var rule = new Border
        {
            Height = 1,
            Background = ResolveBrush("DividerBrush"),
            Width = RuleWidth(),
        };
        _thematicBreaks.Add(rule);

        var paragraph = CreateParagraph();
        paragraph.Margin = new Thickness(0, 8, 0, 8);
        paragraph.Inlines.Add(new InlineUIContainer { Child = rule });
        _target.Blocks.Add(paragraph);
    }

    /// <summary>Rules created by the current document, so a resize can re-width them. Cleared by
    /// <see cref="Reset"/> along with everything else that is per-pass \u2014 the previous pass's
    /// borders are detached from the tree by then and must not be held.</summary>
    private readonly List<Border> _thematicBreaks = new();

    /// <summary>The target's content width, or 0 before first layout \u2014 in which case the
    /// <c>SizeChanged</c> that follows the first arrange fills it in.</summary>
    private double RuleWidth()
    {
        var width = _target.ActualWidth - _target.Padding.Left - _target.Padding.Right;
        return width > 0 ? width : 0;
    }

    private void OnTargetSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_thematicBreaks.Count == 0) return;

        var width = RuleWidth();
        foreach (var rule in _thematicBreaks) rule.Width = width;
    }

    internal void WriteTable(Table table)
    {
        var rows = new List<TableRow>();
        var columnCount = table.ColumnDefinitions?.Count ?? 0;

        foreach (var child in table)
        {
            if (child is TableRow row)
            {
                rows.Add(row);
                var nextColumnIndex = 0;

                foreach (var cellObject in row)
                {
                    if (cellObject is TableCell cell)
                    {
                        var cellColumnIndex = ResolveCellColumnIndex(cell, nextColumnIndex);
                        nextColumnIndex = cellColumnIndex + Math.Max(1, cell.ColumnSpan);
                        columnCount = Math.Max(columnCount, nextColumnIndex);
                    }
                }
            }
        }

        if (rows.Count == 0 || columnCount == 0)
        {
            return;
        }

        var tableGrid = new Grid
        {
            Background = ResolveBrush("SurfaceElevatedBrush"),
        };

        for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            tableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            tableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = rows[rowIndex];
            var nextColumnIndex = 0;

            foreach (var cellObject in row)
            {
                if (cellObject is not TableCell cell)
                {
                    continue;
                }

                var columnIndex = Math.Clamp(
                    ResolveCellColumnIndex(cell, nextColumnIndex),
                    0,
                    columnCount - 1);
                var columnSpan = Math.Min(Math.Max(1, cell.ColumnSpan), columnCount - columnIndex);
                var rowSpan = Math.Min(Math.Max(1, cell.RowSpan), rows.Count - rowIndex);
                nextColumnIndex = columnIndex + columnSpan;
                var cellText = CreateTableCellText(table, cell, columnIndex, row.IsHeader);
                var reachesLastColumn = columnIndex + columnSpan >= columnCount;
                var reachesLastRow = rowIndex + rowSpan >= rows.Count;

                var cellBorder = new Border
                {
                    Background = row.IsHeader ? ResolveBrush("SurfaceSecondaryBrush") : null,
                    BorderBrush = ResolveBrush("DividerBrush"),
                    BorderThickness = new Thickness(0, 0, reachesLastColumn ? 0 : 1, reachesLastRow ? 0 : 1),
                    // RichTextBlock's line box reserves more invisible space above the visible
                    // glyphs than below them, while inline-code UI containers are nudged down to
                    // share the prose baseline. Keep the same 12px total body padding but bias it
                    // downward so the visible text, rather than its asymmetric line box, is
                    // optically centered between the row dividers.
                    Padding = row.IsHeader
                        ? new Thickness(10, 7, 10, 7)
                        : new Thickness(10, 2, 10, 10),
                    Child = cellText,
                };

                Grid.SetColumn(cellBorder, columnIndex);
                Grid.SetColumnSpan(cellBorder, columnSpan);
                Grid.SetRow(cellBorder, rowIndex);
                Grid.SetRowSpan(cellBorder, rowSpan);
                tableGrid.Children.Add(cellBorder);
            }
        }

        var tableBorder = new Border
        {
            Background = ResolveBrush("SurfaceElevatedBrush"),
            BorderBrush = ResolveBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Disabled,
                Content = tableGrid,
            },
        };

        // InlineUIContainer is the only way WinUI's RichTextBlock can host a Grid. Keep the
        // scroller horizontal-only so the outer virtualized message list remains the sole owner
        // of vertical scrolling.
        HighlightedTextBlock.ApplyBaselineNudge(tableBorder, _target.FontSize, fontFamily: _target.FontFamily);
        var paragraph = CreateParagraph();
        paragraph.Margin = new Thickness(0, 7, 0, 7);
        paragraph.Inlines.Add(new InlineUIContainer { Child = tableBorder });
        _target.Blocks.Add(paragraph);
    }

    private static int ResolveCellColumnIndex(TableCell cell, int nextColumnIndex)
    {
        // Pipe-table cells can retain the parser's default ColumnIndex instead of a monotonically
        // increasing visual position. Trust an explicit index only when it does not move back over
        // columns already occupied by earlier cells in this row; otherwise continue sequentially.
        return cell.ColumnIndex >= nextColumnIndex ? cell.ColumnIndex : nextColumnIndex;
    }

    private RichTextBlock CreateTableCellText(Table table, TableCell cell, int columnIndex, bool isHeader)
    {
        var cellText = new RichTextBlock
        {
            FontFamily = _target.FontFamily,
            FontSize = _target.FontSize,
            FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = _target.Foreground,
            IsTextSelectionEnabled = _target.IsTextSelectionEnabled,
            MinWidth = 72,
            MaxWidth = 320,
            TextAlignment = GetTableTextAlignment(table, columnIndex),
            TextWrapping = TextWrapping.WrapWholeWords,
            // A neighbouring cell can make the row much taller (for example, multiline prose
            // beside one inline-code path). Do not stretch this RichTextBlock to that height:
            // size it to its own document and center the whole block inside the table cell.
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var descendant in cell)
        {
            if (descendant is ParagraphBlock cellParagraph)
            {
                var paragraph = new Paragraph();
                if (cellParagraph.Inline is not null)
                {
                    WriteInlineContainer(cellParagraph.Inline, paragraph.Inlines);
                }

                cellText.Blocks.Add(paragraph);
            }
        }

        if (cellText.Blocks.Count == 0)
        {
            cellText.Blocks.Add(new Paragraph());
        }

        return cellText;
    }

    private static TextAlignment GetTableTextAlignment(Table table, int columnIndex)
    {
        if (table.ColumnDefinitions is null || columnIndex >= table.ColumnDefinitions.Count)
        {
            return TextAlignment.Left;
        }

        return table.ColumnDefinitions[columnIndex].Alignment switch
        {
            TableColumnAlign.Center => TextAlignment.Center,
            TableColumnAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
    }

    internal void WriteHtmlBlock(HtmlBlock block)
        => WritePlainParagraph(block.Lines.ToString());

    internal void WriteLiteral(LiteralInline inline)
        => WriteHighlightedText(inline.Content.ToString());

    internal void WriteLineBreak(LineBreakInline inline)
        => CurrentInlines.Add(new LineBreak());

    internal void WriteCodeInline(CodeInline inline)
    {
        // Inside a Hyperlink there is no room for the Border-in-an-InlineUIContainer treatment
        // below; fall back to a plain monospace span so `code` in a link still renders as code.
        if (!CanHostUIContainer)
        {
            var codeSpan = new Span
            {
                FontFamily = CodeFont,
                Foreground = ResolveBrush("CodeTextBrush"),
            };
            WriteHighlightedText(
                inline.Content, codeSpan.Inlines, allowInlineUIContainers: false, fontFamily: CodeFont);
            CurrentInlines.Add(codeSpan);
            return;
        }

        // RichTextBlock inlines (Run/Span) can't have a Background, so — same
        // technique as HighlightedTextBlock's search-match spans — wrap a Border
        // in an InlineUIContainer to give inline code a subtle fill distinct from
        // both surrounding prose and fenced code blocks (see WriteCodeBlockLines).
        //
        // The code itself goes in a nested RichTextBlock, not a TextBlock: a search
        // match inside the code is highlighted with its own InlineUIContainer, which
        // only a RichTextBlock paragraph may host.
        var codeText = new RichTextBlock
        {
            FontFamily = CodeFont,
            FontSize = _target.FontSize,
            Foreground = ResolveBrush("CodeTextBrush"),
        };
        var codeParagraph = new Paragraph();
        WriteHighlightedText(inline.Content, codeParagraph.Inlines, fontFamily: CodeFont);
        codeText.Blocks.Add(codeParagraph);

        var border = new Border
        {
            Background = ResolveBrush("InlineCodeBackgroundBrush"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Child = codeText,
        };

        // Drop the chip back onto the text baseline. Here we are aligning the code *text* with the
        // prose text, so the offset is the code font's own descender plus the padding under it.
        HighlightedTextBlock.ApplyBaselineNudge(
            border, _target.FontSize, bottomPadding: 1, fontFamily: CodeFont);
        CurrentInlines.Add(new InlineUIContainer { Child = border });
    }

    /// <summary>
    /// <c>**bold**</c>, <c>*italic*</c>, and the two tilde forms the advanced extensions add.
    ///
    /// <para><c>~~struck~~</c> gets a real strikethrough. It used to share the single-tilde
    /// (subscript) treatment — grey italic — which reads as ordinary emphasis, so a plan that
    /// crossed an item out looked exactly like one that stressed it. <c>TextDecorations</c> is a
    /// <c>TextElement</c> property, so a Span can carry it without an InlineUIContainer.</para>
    /// </summary>
    internal void WriteEmphasis(EmphasisInline inline)
    {
        var isStrikethrough = inline.DelimiterChar == '~' && inline.DelimiterCount >= 2;

        Span container = inline.DelimiterChar switch
        {
            '~' => new Span(),
            _ when inline.DelimiterCount >= 2 => new Bold(),
            _ => new Italic(),
        };

        if (isStrikethrough)
        {
            container.TextDecorations = TextDecorations.Strikethrough;
            // Struck text is de-emphasised but still readable: keep the body colour rather than
            // dropping to secondary on top of the line, which stacks two "less important" signals.
        }
        else if (inline.DelimiterChar == '~')
        {
            // Single tilde is the subscript extension. No real subscript in a RichTextBlock inline,
            // so it keeps the pre-existing quiet-italic approximation.
            container.FontStyle = FontStyle.Italic;
            container.Foreground = ResolveBrush("TextFillColorSecondaryBrush");
        }

        WriteNestedInlines(inline, container.Inlines);
        CurrentInlines.Add(container);
    }

    /// <summary>Renders the <c>[ ]</c>/<c>[x]</c> marker of a GFM task-list item.</summary>
    internal void WriteTaskList(TaskList inline)
        => CurrentInlines.Add(new Run
        {
            // U+2611 / U+2610. Written as a text Run rather than a real CheckBox: these are a
            // record of what the agent wrote, not controls the user can toggle, and a focusable
            // control per line would also add a tab stop to every checklist row.
            Text = inline.Checked ? "☑ " : "☐ ",
            Foreground = ResolveBrush(
                inline.Checked ? "BrandPrimaryBrush" : "TextFillColorTertiaryBrush"),
        });

    internal void WriteLink(LinkInline inline)
    {
        // An image reference renders as a clickable link labelled with its alt text, not as the
        // raw URL this used to print. Two reasons it is not fetched and shown inline: images the
        // agent actually sends arrive as `agent_image` events and go through
        // AttachmentImageCacheService (hashed, size-capped, written to the local cache before they
        // ever reach a message), and auto-fetching whatever URL appears in agent markdown would be
        // a new outbound request the user never asked for. Falling back to the URL keeps a
        // reference with no alt text reachable rather than rendering nothing.
        if (inline.IsImage)
        {
            var alt = GetPlainText(inline);
            WriteImageReference(
                string.IsNullOrWhiteSpace(alt) ? inline.Url ?? string.Empty : alt,
                inline.Url);
            return;
        }

        var hyperlink = new Hyperlink
        {
            Foreground = ResolveBrush("TextLinkBrush"),
        };

        var navigateUri = CreateUri(inline.Url);
        if (navigateUri is not null)
        {
            hyperlink.Click += async (_, _) => await Launcher.LaunchUriAsync(navigateUri);
        }

        // A Hyperlink may only contain text inlines, so nothing written inside one — link text,
        // a search highlight, inline code — may use an InlineUIContainer.
        if (inline.FirstChild is not null)
        {
            WriteNestedInlines(inline, hyperlink.Inlines, blocksUIContainers: true);
        }
        else
        {
            WriteHighlightedText(inline.Url ?? string.Empty, hyperlink.Inlines, allowInlineUIContainers: false);
        }

        CurrentInlines.Add(hyperlink);
    }

    /// <summary>Flattens an inline's literal descendants, for the places that need the text of a
    /// node rather than its rendering — today the alt text of an image.</summary>
    private static string GetPlainText(ContainerInline container)
    {
        var text = new System.Text.StringBuilder();
        Collect(container);
        return text.ToString().Trim();

        void Collect(ContainerInline node)
        {
            foreach (var child in node)
            {
                switch (child)
                {
                    case LiteralInline literal: text.Append(literal.Content.ToString()); break;
                    case CodeInline code: text.Append(code.Content); break;
                    case LineBreakInline: text.Append(' '); break;
                    case ContainerInline nested: Collect(nested); break;
                }
            }
        }
    }

    private void WriteImageReference(string label, string? url)
    {
        var navigateUri = CreateUri(url);
        if (navigateUri is null)
        {
            WriteHighlightedText(label);
            return;
        }

        var hyperlink = new Hyperlink { Foreground = ResolveBrush("TextLinkBrush") };
        hyperlink.Click += async (_, _) => await Launcher.LaunchUriAsync(navigateUri);
        WriteHighlightedText(label, hyperlink.Inlines, allowInlineUIContainers: false);
        CurrentInlines.Add(hyperlink);
    }

    internal void WriteContainerInline(ContainerInline inline)
        => WriteNestedInlines(inline, CurrentInlines);

    /// <summary>
    /// Raw HTML the parser could not turn into markdown. The tag itself is never shown: it used to
    /// be written out verbatim, so an agent's <c>&lt;b&gt;text&lt;/b&gt;</c> rendered with the
    /// angle brackets visible and a <c>&lt;br&gt;</c> inside a table cell appeared as literal
    /// characters instead of a break. Dropping the tag leaves the text between tags, which is the
    /// content; <c>&lt;br&gt;</c> is the one tag with a document meaning here, so it is honoured.
    /// </summary>
    internal void WriteHtmlInline(HtmlInline inline)
    {
        var tag = inline.Tag?.Trim() ?? string.Empty;
        if (tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase))
        {
            CurrentInlines.Add(new LineBreak());
        }
    }

    private InlineCollection CurrentInlines => _inlineScopes.Peek();

    private Paragraph CreateParagraph()
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 4, 0, 4),
        };

        if (_quoteDepth > 0)
        {
            paragraph.FontStyle = FontStyle.Italic;
            paragraph.Foreground = ResolveBrush("TextFillColorSecondaryBrush");
            paragraph.Margin = new Thickness(6 + ((_quoteDepth - 1) * 14), 4, 0, 4);
            paragraph.Inlines.Add(new Run
            {
                Text = string.Concat(System.Linq.Enumerable.Repeat("\u258e ", _quoteDepth)),
                Foreground = ResolveBrush("BrandPrimaryBrush"),
            });
        }

        return paragraph;
    }

    private void WriteListItem(ListItemBlock item, string prefix)
    {
        var firstRenderableBlock = true;
        foreach (var child in item)
        {
            switch (child)
            {
                case ParagraphBlock paragraph:
                    WriteParagraphBlock(paragraph, firstRenderableBlock ? prefix : "  ");
                    firstRenderableBlock = false;
                    break;
                case FencedCodeBlock fencedCode:
                    if (firstRenderableBlock)
                    {
                        WritePlainParagraph(prefix.TrimEnd());
                        firstRenderableBlock = false;
                    }
                    WriteFencedCodeBlock(fencedCode);
                    break;
                case CodeBlock codeBlock:
                    if (firstRenderableBlock)
                    {
                        WritePlainParagraph(prefix.TrimEnd());
                        firstRenderableBlock = false;
                    }
                    WriteCodeBlock(codeBlock);
                    break;
                default:
                    if (firstRenderableBlock)
                    {
                        WritePlainParagraph(prefix.TrimEnd());
                        firstRenderableBlock = false;
                    }
                    Write(child);
                    break;
            }
        }
    }

    private void WriteCodeBlockLines(string code, string? language = null)
    {
        // Give fenced/indented code blocks a real background + border so they read
        // as a distinct layer from surrounding prose. WinUI 3's RichTextBlock has no
        // BlockUIContainer (unlike WPF/UWP) and Paragraph itself has no Background,
        // so — same InlineUIContainer trick as the inline-code Border above and
        // HighlightedTextBlock's search-match spans — this drops a single Border
        // into its own paragraph as one inline UI element.
        // Nested RichTextBlock rather than TextBlock, for the same reason as inline code:
        // a highlighted search match is an InlineUIContainer, which a TextBlock rejects.
        var codeText = new RichTextBlock
        {
            FontFamily = CodeFont,
            FontSize = _target.FontSize,
            Foreground = ResolveBrush("CodeTextBrush"),
            TextWrapping = TextWrapping.NoWrap,
        };
        var codeParagraph = new Paragraph();
        WriteHighlightedText(code.TrimEnd(), codeParagraph.Inlines, fontFamily: CodeFont);
        codeText.Blocks.Add(codeParagraph);

        var codeScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            // WinUI expands an auto-hiding horizontal scrollbar over the bottom of its viewport.
            // Keep the last code line above that overlay so grabbing the thumb never obscures it.
            Padding = new Thickness(
                10,
                CodeBlockVerticalPadding,
                10,
                CodeBlockVerticalPadding + CodeBlockScrollBarClearance),
            Content = codeText,
        };

        UIElement codeContent = codeScroller;
        if (!string.IsNullOrEmpty(language))
        {
            var codeGrid = new Grid();
            codeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            codeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var languageHeader = new Border
            {
                Background = ResolveBrush("SurfaceSecondaryBrush"),
                BorderBrush = ResolveBrush("DividerBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 5, 10, 5),
                Child = new TextBlock
                {
                    FontFamily = CodeFont,
                    FontSize = Math.Max(11, _target.FontSize - 2),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
                    Text = language,
                },
            };

            Grid.SetRow(languageHeader, 0);
            Grid.SetRow(codeScroller, 1);
            codeGrid.Children.Add(languageHeader);
            codeGrid.Children.Add(codeScroller);
            codeContent = codeGrid;
        }

        var border = new Border
        {
            Background = ResolveBrush("CodeBackgroundBrush"),
            BorderBrush = ResolveBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = codeContent,
        };

        // The block's own (otherwise empty) line still has a baseline, and the container hangs the
        // border's bottom edge off it — so without this the whole block rides up over its line and
        // the gap beneath it looks too big. This aligns the *box* to the surrounding line, not the
        // text inside it: hence the document's font, and no bottom-padding term.
        HighlightedTextBlock.ApplyBaselineNudge(border, _target.FontSize, fontFamily: _target.FontFamily);

        var paragraph = CreateParagraph();
        paragraph.Margin = new Thickness(0, 6, 0, 6);
        paragraph.Inlines.Add(new InlineUIContainer { Child = border });
        _target.Blocks.Add(paragraph);
    }

    private static string? GetCodeLanguage(FencedCodeBlock block)
    {
        var info = block.Info?.ToString()?.Trim() ?? string.Empty;
        if (info.Length == 0)
        {
            return null;
        }

        var separator = info.IndexOfAny(' ', '\t', '{');
        var language = separator >= 0 ? info[..separator] : info;
        return language.Length == 0 ? null : language;
    }

    private void WritePlainParagraph(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var paragraph = CreateParagraph();
        WriteHighlightedText(text.TrimEnd(), paragraph.Inlines);
        _target.Blocks.Add(paragraph);
    }

    private void WriteHighlightedText(string text)
        => WriteHighlightedText(text, CurrentInlines, CanHostUIContainer);

    /// <summary>Set <paramref name="allowInlineUIContainers"/> to false whenever the target is not
    /// a Paragraph of the RichTextBlock — see <see cref="HighlightedTextBlock.AddHighlightedRuns"/>.
    /// <paramref name="fontFamily"/> is the face the text renders in (the code font inside a code
    /// container, otherwise the document's own); it governs both the highlight's face and how far
    /// it has to drop to sit on the baseline.</summary>
    private void WriteHighlightedText(
        string text, InlineCollection inlines, bool allowInlineUIContainers = true, FontFamily? fontFamily = null)
        => HighlightedTextBlock.AddHighlightedRuns(
            inlines, text, _searchQuery, _activeMatchIndex, ref _matchIndex,
            allowInlineUIContainers, _target.FontSize, fontFamily ?? _target.FontFamily);

    private void WriteInlineContainer(ContainerInline container, InlineCollection inlines)
        => WriteNestedInlines(container, inlines);

    /// <param name="blocksUIContainers">True when <paramref name="targetInlines"/> belongs to an
    /// element that rejects an <see cref="InlineUIContainer"/> (a Hyperlink), so everything written
    /// while this scope is on the stack falls back to text-only highlighting.</param>
    private void WriteNestedInlines(
        ContainerInline container, InlineCollection targetInlines, bool blocksUIContainers = false)
    {
        _inlineScopes.Push(targetInlines);
        if (blocksUIContainers) _uiContainerBlockedDepth++;
        try
        {
            for (Markdig.Syntax.Inlines.Inline? child = container.FirstChild; child is not null; child = child.NextSibling)
            {
                Write(child);
            }
        }
        finally
        {
            if (blocksUIContainers) _uiContainerBlockedDepth--;
            _inlineScopes.Pop();
        }
    }

    private static Uri? CreateUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    }

    private SolidColorBrush ResolveBrush(string key)
    {
        var colorKey = key switch
        {
            "TextFillColorSecondaryBrush" => "TextSecondaryColor",
            "TextFillColorTertiaryBrush" => "TextTertiaryColor",
            "TextLinkBrush" => "TextLinkColor",
            "BrandPrimaryBrush" => "BrandPrimaryColor",
            "CodeTextBrush" => "CodeTextColor",
            "InlineCodeBackgroundBrush" => "InlineCodeBackgroundColor",
            "CodeBackgroundBrush" => "CodeBackgroundColor",
            "BorderSubtleBrush" => "BorderSubtleColor",
            "DividerBrush" => "DividerColor",
            "SurfaceElevatedBrush" => "SurfaceElevatedColor",
            "SurfaceSecondaryBrush" => "SurfaceSecondaryColor",
            _ => throw new System.Collections.Generic.KeyNotFoundException(
                $"Markdown theme brush '{key}' has no color-token mapping."),
        };

        // Shared instance per (token, theme), not a fresh brush per element. ResolveBrush is called
        // for every paragraph, quote, link, code span, code block and table cell, so a reply with a
        // few dozen nodes used to mint a few dozen SolidColorBrushes — each a DependencyObject with
        // a native peer — and walk Application.Current.Resources.MergedDictionaries once apiece.
        // Nothing here mutates the brush it gets back, which is what makes sharing safe.
        var isDark = _target.ActualTheme == ElementTheme.Dark;
        return ThemeService.GetBrush(colorKey, isDark);
    }

}
