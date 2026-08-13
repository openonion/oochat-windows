using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// A horizontal "icon + label" pair, the app's standard button content. Icon
/// and text pick up the inherited <see cref="Control.Foreground"/> and
/// <see cref="Control.FontSize"/>; <see cref="Glyph"/>, <see cref="Text"/>,
/// <see cref="IconSize"/> and <see cref="Spacing"/> are set per site.
/// </summary>
public sealed partial class IconText : UserControl
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(
            nameof(Glyph),
            typeof(Icon),
            typeof(IconText),
            new PropertyMetadata(default(Icon)));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(IconText),
            new PropertyMetadata(""));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(
            nameof(IconSize),
            typeof(IconSize),
            typeof(IconText),
            // Size16 is the default on purpose, not just as a common size: IconSize selects a
            // separate font file per size, and the smaller ones are incomplete (Size12 ships 439
            // glyphs against Size16's 4372). A missing glyph renders as a blank box with no
            // build error, so anything smaller than 16 must be a scaled-down Size16 rather than
            // a genuine small size — see the FluentIcons notes in CLAUDE.md.
            new PropertyMetadata(FluentIcons.Common.IconSize.Size16));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(
            nameof(Spacing),
            typeof(double),
            typeof(IconText),
            new PropertyMetadata(8.0));

    public IconText()
    {
        InitializeComponent();
    }

    public Icon Glyph
    {
        get => (Icon)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IconSize IconSize
    {
        get => (IconSize)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }
}
