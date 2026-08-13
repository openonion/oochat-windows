using System;
using System.Collections.Generic;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace ConnectOnion.WinUIClient.Controls;

public sealed class HighlightedTextBlock : UserControl
{
    private readonly RichTextBlock _textBlock;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(HighlightedTextBlock),
            new PropertyMetadata(string.Empty, OnRenderPropertyChanged));

    public static readonly DependencyProperty SearchQueryProperty =
        DependencyProperty.Register(
            nameof(SearchQuery),
            typeof(string),
            typeof(HighlightedTextBlock),
            new PropertyMetadata(string.Empty, OnRenderPropertyChanged));

    public static readonly DependencyProperty ActiveMatchIndexProperty =
        DependencyProperty.Register(
            nameof(ActiveMatchIndex),
            typeof(int),
            typeof(HighlightedTextBlock),
            new PropertyMetadata(-1, OnRenderPropertyChanged));

    public HighlightedTextBlock()
    {
        _textBlock = new RichTextBlock
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
        };

        // Synced by hand, not bound — same reason as MarkdownTextBlock: `UserControl` has no
        // default style, so `Foreground` is null unless a caller sets it, and forwarding a null
        // Brush means "do not paint" rather than "use the default". Today the one call site
        // (ChatPage's user bubble) does set it, so this is a trap rather than a live bug — but
        // it is the same trap that made every plan and tool-log body render invisible, and the
        // symptom (text that only appears while selected) points nowhere near the cause.
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => SyncForeground());
        ActualThemeChanged += (_, _) => SyncForeground();
        SyncForeground();

        _textBlock.SetBinding(RichTextBlock.FontSizeProperty, new Binding
        {
            Path = new PropertyPath(nameof(FontSize)),
            Source = this,
        });

        Content = _textBlock;

        // Search-highlight colors are theme-aware (Styles/Colors.xaml SearchMatch*/
        // SearchCurrentBackground* tokens), resolved fresh on every render — force a
        // re-render on a live theme flip so visible matches don't stay stuck on the
        // old theme's colors.
        ActualThemeChanged += (_, _) => { _renderedRuns = false; Render(); };
        Loaded += (_, _) => Render();
        Unloaded += (_, _) =>
        {
            _textBlock.Blocks.Clear();
            _renderedRuns = false;
        };
    }

    // Mirrors the guard in MarkdownTextBlock, for the same reason: Text, SearchQuery and
    // ActiveMatchIndex all funnel into Render, and a find pass writes the query twice in quick
    // succession (cleared, then re-applied), so each user bubble rebuilt its inlines twice per
    // keystroke. False whenever the runs on screen do not match the fields below.
    private bool _renderedRuns;
    private string _renderedText = string.Empty;
    private string _renderedQuery = string.Empty;
    private int _renderedActiveMatchIndex = -1;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string SearchQuery
    {
        get => (string)GetValue(SearchQueryProperty);
        set => SetValue(SearchQueryProperty, value);
    }

    public int ActiveMatchIndex
    {
        get => (int)GetValue(ActiveMatchIndexProperty);
        set => SetValue(ActiveMatchIndexProperty, value);
    }

    private static void OnRenderPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((HighlightedTextBlock)d).Render();

    /// <summary>Pushes an always-paintable foreground onto the inner <see cref="RichTextBlock"/>.
    /// See the note in the constructor for why this is not a binding.</summary>
    private void SyncForeground()
        => _textBlock.Foreground =
            Foreground ?? Presentation.ThemeBrushResolver.Resolve("TextPrimaryBrush");

    private void Render()
    {
        var text = Text ?? string.Empty;
        var query = SearchQuery ?? string.Empty;

        if (_renderedRuns
            && string.Equals(_renderedText, text, StringComparison.Ordinal)
            && string.Equals(_renderedQuery, query, StringComparison.Ordinal)
            && _renderedActiveMatchIndex == ActiveMatchIndex)
        {
            return;
        }

        _textBlock.Blocks.Clear();
        _renderedRuns = false;
        if (!IsLoaded) return;

        _renderedRuns = true;
        _renderedText = text;
        _renderedQuery = query;
        _renderedActiveMatchIndex = ActiveMatchIndex;

        var paragraph = new Paragraph();
        _textBlock.Blocks.Add(paragraph);

        AddHighlightedRuns(
            paragraph.Inlines, text, query, ActiveMatchIndex,
            fontSize: _textBlock.FontSize, fontFamily: _textBlock.FontFamily);
    }

    /// <summary>Last-resort descender ratio, used only if a font's real metrics can't be measured
    /// (Segoe UI's descender is ~0.21 em, so this is close for the default UI font and merely
    /// approximate for anything else — which is exactly why it is not the primary path).</summary>
    private const double FallbackDescentRatio = 0.22;

    /// <summary>Measured descents, keyed by font family + size. Measuring is cheap but not free,
    /// and the renderer asks for the same handful of (family, size) pairs on every re-render.</summary>
    private static readonly Dictionary<(string Family, double Size), double> DescentCache = new();
    private const int MaxDescentCacheEntries = 32;

    /// <summary>
    /// The real descender height of <paramref name="fontFamily"/> at <paramref name="fontSize"/>,
    /// in DIPs — obtained by measuring a probe <see cref="TextBlock"/> and subtracting its
    /// <see cref="TextBlock.BaselineOffset"/> (distance from the top of the line to the baseline)
    /// from its measured height. This replaces a guessed em-ratio with the font's actual metrics,
    /// so changing the UI font or mixing in a monospace face stays correctly aligned with no
    /// constant to re-tune.
    /// </summary>
    internal static double DescentFor(FontFamily? fontFamily, double fontSize)
    {
        if (fontSize <= 0) return 0;

        var key = (fontFamily?.Source ?? string.Empty, fontSize);
        if (DescentCache.TryGetValue(key, out var cached)) return cached;

        var descent = FallbackDescentRatio * fontSize;

        try
        {
            // "Hg" spans the full ascender-to-descender range, so the measured height is the real
            // line height rather than the height of whatever glyphs happened to be on screen.
            var probe = new TextBlock { Text = "Hg", FontSize = fontSize };
            if (fontFamily is not null) probe.FontFamily = fontFamily;

            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

            var measured = probe.DesiredSize.Height - probe.BaselineOffset;
            if (measured > 0 && !double.IsNaN(measured) && !double.IsInfinity(measured))
                descent = measured;
        }
        catch
        {
            // Measuring needs the UI thread and a live XAML runtime; if either is missing, the
            // approximation above is still far better than no nudge at all.
        }

        // Font families can come from user preferences and font sizes from zoom. This cache is a
        // rendering accelerator, not durable state; bound it so a long session experimenting
        // with many combinations cannot leave one entry per combination forever.
        if (DescentCache.Count >= MaxDescentCacheEntries) DescentCache.Clear();
        DescentCache[key] = descent;
        return descent;
    }

    /// <summary>
    /// An <see cref="InlineUIContainer"/> aligns the <i>bottom edge</i> of its child with the
    /// surrounding text's <i>baseline</i> — not with the bottom of the text — so anything hosted in
    /// one sits a descender too high and visibly floats above the line. Push it back down by that
    /// descent, plus whatever padding sits beneath the child's own text.
    ///
    /// This is a <see cref="RenderTransform"/> on purpose: it shifts the element without changing
    /// the line box, so re-aligning a highlight can't reflow the paragraph it sits in.
    /// </summary>
    /// <param name="fontFamily">The font whose descender governs the alignment: the font of the text
    /// <i>inside</i> the container when aligning that text (inline code), or the font of the
    /// surrounding prose when aligning the box itself (a code block).</param>
    internal static void ApplyBaselineNudge(
        FrameworkElement element, double fontSize, double bottomPadding = 0, FontFamily? fontFamily = null)
        => element.RenderTransform = new TranslateTransform
        {
            Y = Math.Round(DescentFor(fontFamily, fontSize) + bottomPadding),
        };

    /// <summary>
    /// Appends <paramref name="text"/> to <paramref name="inlines"/>, wrapping each occurrence of
    /// <paramref name="query"/> in a highlight.
    ///
    /// <paramref name="allowInlineUIContainers"/> must be false when the target collection is not
    /// a <see cref="Paragraph"/> inside a <see cref="RichTextBlock"/>: an
    /// <see cref="InlineUIContainer"/> is only legal there, and both <c>TextBlock.Inlines</c> and
    /// <c>Hyperlink.Inlines</c> throw <c>ArgumentException("Value does not fall within the
    /// expected range")</c> on one. Those targets get a bold/underlined <see cref="Run"/> instead
    /// — no background fill, but the match is still visible and still counted, so find navigation
    /// stays in step with the highlights that do get a fill.
    /// </summary>
    internal static void AddHighlightedRuns(
        InlineCollection inlines,
        string text,
        string query,
        int activeMatchIndex,
        ref int matchIndex,
        bool allowInlineUIContainers = true,
        double fontSize = 14,
        FontFamily? fontFamily = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            inlines.Add(new Run { Text = text });
            return;
        }

        var cursor = 0;
        while (cursor < text.Length)
        {
            var index = text.IndexOf(query, cursor, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                inlines.Add(new Run { Text = text[cursor..] });
                return;
            }

            if (index > cursor)
            {
                inlines.Add(new Run { Text = text[cursor..index] });
            }

            var isActive = matchIndex == activeMatchIndex;
            var matched = text.Substring(index, query.Length);

            if (!allowInlineUIContainers)
            {
                inlines.Add(new Underline
                {
                    Inlines =
                    {
                        new Run
                        {
                            Text = matched,
                            FontWeight = isActive ? FontWeights.Bold : FontWeights.SemiBold,
                        },
                    },
                });
                matchIndex++;
                cursor = index + query.Length;
                continue;
            }

            // WinUI 3 inline text elements (Run, Span) lack a Background property.
            // Use InlineUIContainer with a Border to render the highlight background.
            // Resolved fresh (not cached) so this always reflects the current theme —
            // see the ActualThemeChanged handler in the constructor above.
            var border = new Border
            {
                Background = (Brush)Application.Current.Resources[
                    isActive ? "SearchCurrentBackgroundBrush" : "SearchMatchBackgroundBrush"],
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(2, 0, 2, 0),
            };

            var matchText = new TextBlock
            {
                Text = matched,
                // Match the surrounding text's size and face. A TextBlock inside an
                // InlineUIContainer inherits neither, so without this a highlighted word renders at
                // the default 14px UI font — visibly larger than 13px prose, and proportional in
                // the middle of a monospace code block.
                FontSize = fontSize,
                // The current match is an ochre-red fill wanting light text; a plain match
                // is a yellow fill wanting dark text — two different foregrounds, not one.
                Foreground = (Brush)Application.Current.Resources[
                    isActive ? "SearchCurrentTextBrush" : "SearchMatchTextBrush"],
                FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal,
            };
            if (fontFamily is not null) matchText.FontFamily = fontFamily;

            border.Child = matchText;
            ApplyBaselineNudge(border, fontSize, fontFamily: fontFamily);
            inlines.Add(new InlineUIContainer { Child = border });
            matchIndex++;
            cursor = index + query.Length;
        }
    }

    private static void AddHighlightedRuns(
        InlineCollection inlines, string text, string query, int activeMatchIndex,
        double fontSize, FontFamily? fontFamily)
    {
        var matchIndex = 0;
        AddHighlightedRuns(
            inlines, text, query, activeMatchIndex, ref matchIndex,
            fontSize: fontSize, fontFamily: fontFamily);
    }
}
