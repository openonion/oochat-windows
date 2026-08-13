using ConnectOnion.WinUIClient.Services.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// Renders a tool step's result body: JSON arrives minified and is re-indented and syntax
/// coloured, anything else is shown exactly as the tool sent it.
///
/// <para>The decision and the tokenizing both live in <see cref="ToolResultFormatter"/> (Core,
/// unit-tested); this class only turns tokens into <see cref="Run"/>s. Keeping the split there
/// means the formatting rules are testable and this file stays a thin, obvious mapping.</para>
///
/// <para>Everything wraps, and nothing scrolls sideways. A long value inside re-indented JSON
/// therefore folds onto the next line instead of pushing the card wider — the indentation still
/// carries the structure down the left edge, which is the part worth reading, and a horizontal
/// scrollbar inside a chat bubble is worse than a folded line.</para>
/// </summary>
/// <remarks>
/// <see cref="UserControl"/>, not <see cref="ContentControl"/>: a class deriving straight from
/// <c>ContentControl</c> gets no default style — templates are resolved per concrete type, and
/// without a <c>DefaultStyleKey</c> plus a <c>generic.xaml</c> entry there is no
/// <c>ControlTemplate</c>, so the content never renders and the card shows an empty box.
/// <c>UserControl</c> carries a template that presents <see cref="ContentControl.Content"/>,
/// which is why <see cref="HighlightedTextBlock"/> is built the same way.
/// </remarks>
public sealed class ToolResultTextBlock : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ToolResultTextBlock),
        new PropertyMetadata(null, OnTextChanged));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private readonly TextBlock _textBlock = new()
    {
        FontFamily = new FontFamily(
            "ms-appx:///Assets/Fonts/JetBrainsMonoNerdFontMono-Regular.ttf#JetBrainsMono NFM"),
        FontSize = 13,
        IsTextSelectionEnabled = true,
        // Always on: the log region is a fixed-width column, so anything that would overflow folds
        // rather than scrolling sideways.
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 20,
    };

    public ToolResultTextBlock()
    {
        Content = _textBlock;
        IsTabStop = false;

        // Colours are resolved by key at render time, so a live theme flip has to re-run the
        // render rather than leaving the previous theme's runs on screen — the same contract
        // HighlightedTextBlock follows.
        ActualThemeChanged += (_, _) => Render();
        Loaded += (_, _) => Render();
        Unloaded += (_, _) => _textBlock.Inlines.Clear();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ToolResultTextBlock)d).Render();

    private void Render()
    {
        _textBlock.Inlines.Clear();
        if (!IsLoaded) return;

        var formatted = ToolResultFormatter.Format(Text);

        // Re-indented JSON sits tighter: its lines are short, indented and visually grouped, so
        // the leading that keeps a wall of console text readable only pulls them apart.
        _textBlock.LineHeight = formatted.Format == ResultFormat.Json ? 19 : 20;

        foreach (var token in formatted.Tokens)
        {
            var run = new Run
            {
                Text = token.Text,
                Foreground = BrushFor(token.Kind),
            };
            // Weight, not just colour, on the two lines that structure a transcript — colour
            // alone is a weak signal in a monospace wall, and it is the only signal a
            // colour-blind reader would be left with.
            if (token.Kind is ResultTokenKind.Command or ResultTokenKind.Failure)
                run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;

            _textBlock.Inlines.Add(run);
        }
    }

    /// <summary>Resolved fresh per run rather than cached, so a theme flip picks up the new
    /// palette (the app re-points these keys per theme; a held reference would go stale).</summary>
    private Brush BrushFor(ResultTokenKind kind)
    {
        var key = kind switch
        {
            ResultTokenKind.Key => "SyntaxKeyBrush",
            ResultTokenKind.StringLiteral => "SyntaxStringBrush",
            ResultTokenKind.Number => "SyntaxNumberBrush",
            ResultTokenKind.Keyword => "SyntaxKeywordBrush",
            ResultTokenKind.Punctuation => "SyntaxPunctuationBrush",
            ResultTokenKind.Command => "SyntaxCommandBrush",
            ResultTokenKind.Label => "SyntaxLabelBrush",
            ResultTokenKind.Failure => "SyntaxFailureBrush",
            _ => "TextPrimaryBrush",
        };

        return Application.Current.Resources[key] as Brush
            ?? Application.Current.Resources["TextPrimaryBrush"] as Brush
            ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
