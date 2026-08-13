using System.Globalization;
using ConnectOnion.WinUIClient.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ConnectOnion.WinUIClient.Controls;

/// <summary>
/// A year of daily token usage as a GitHub-style contributions grid: one column per week, one row
/// per weekday, colour by intensity.
///
/// <para>All of the layout maths — the window, the week padding, the intensity buckets, the month
/// label positions — lives in <see cref="UsageHeatmap"/> in the Core project, where it is unit
/// tested without a window. This control only renders what it is handed, which is why it has no
/// date logic of its own.</para>
///
/// <para>The two label strips are drawn onto <see cref="Canvas"/>es from code rather than declared
/// in XAML, because both are positioned against the grid's cell stride: a month name has to sit
/// above the column its month starts in, and a weekday name beside its row. Expressing that in
/// markup would mean duplicating the geometry in a second place that could silently drift out of
/// step with the squares.</para>
/// </summary>
public sealed partial class UsageHeatmapView : UserControl
{
    /// <summary>Row indices that get a weekday label. Mon/Wed/Fri, as the design specifies —
    /// weeks start on Monday, so these are rows 0, 2 and 4.</summary>
    private static readonly (int Row, string Label)[] WeekdayLabels =
    [
        (0, "Mon"), (2, "Wed"), (4, "Fri"),
    ];

    public UsageHeatmapView()
    {
        InitializeComponent();
        // Both strips are re-measured against live resource values, so they are built on Loaded
        // rather than in the constructor: StaticResource lookups and the text metrics they depend
        // on are not reliable before the control is in the tree.
        Loaded += (_, _) => Render();
    }

    /// <summary>The grid to display. Setting it re-renders; null clears.</summary>
    public UsageHeatmap? Heatmap
    {
        get => (UsageHeatmap?)GetValue(HeatmapProperty);
        set => SetValue(HeatmapProperty, value);
    }

    public static readonly DependencyProperty HeatmapProperty =
        DependencyProperty.Register(
            nameof(Heatmap),
            typeof(UsageHeatmap),
            typeof(UsageHeatmapView),
            new PropertyMetadata(null, OnHeatmapChanged));

    private static void OnHeatmapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((UsageHeatmapView)d).Render();

    private double CellStride => (double)Resources["HeatmapCellStride"];

    private void Render()
    {
        // Render runs from both the Loaded handler and the property-changed callback, and either
        // can arrive first. Bailing until the template is realised keeps the second one correct
        // instead of throwing on a null Canvas.
        if (!IsLoaded || MonthStrip is null || WeekdayGutter is null) return;

        var map = Heatmap;
        DayRepeater.ItemsSource = map?.Days;

        MonthStrip.Children.Clear();
        WeekdayGutter.Children.Clear();

        if (map is null)
        {
            SummaryText.Text = "";
            RangeText.Text = "";
            return;
        }

        SummaryText.Text = map.IsEmpty
            ? "No token usage recorded yet"
            : $"{FormatTokens(map.TotalTokens)} tokens over {map.ActiveDayCount} " +
              $"{(map.ActiveDayCount == 1 ? "active day" : "active days")}";

        RangeText.Text =
            $"{map.Start.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} – " +
            $"{map.End.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";

        MonthStrip.Width = map.WeekCount * CellStride;
        RenderMonthLabels(map);
        RenderWeekdayLabels();
    }

    private void RenderMonthLabels(UsageHeatmap map)
    {
        // A label is skipped when its month starts within ~2 columns of the previous one, which
        // happens whenever a short month's first Monday lands early. Drawing both would overlap
        // the text; the design's reference output shows the same gaps (Jul, then a space, Aug).
        const double minimumGapColumns = 3;
        var lastPlacedColumn = double.NegativeInfinity;

        foreach (var month in map.Months)
        {
            if (month.WeekIndex - lastPlacedColumn < minimumGapColumns) continue;
            lastPlacedColumn = month.WeekIndex;

            var label = new TextBlock
            {
                Text = month.Label,
                Style = Application.Current.Resources["ProductCaptionTextStyle"] as Style,
                Foreground = Brush("TextSecondaryBrush"),
            };
            Canvas.SetLeft(label, month.WeekIndex * CellStride);
            Canvas.SetTop(label, 0);
            MonthStrip.Children.Add(label);
        }
    }

    private void RenderWeekdayLabels()
    {
        foreach (var (row, text) in WeekdayLabels)
        {
            var label = new TextBlock
            {
                Text = text,
                Style = Application.Current.Resources["ProductCaptionTextStyle"] as Style,
                Foreground = Brush("TextSecondaryBrush"),
            };
            Canvas.SetLeft(label, 0);
            // Nudged up by a pixel so the caption's cap height optically centres against
            // an 11px square rather than sitting a touch low.
            Canvas.SetTop(label, (row * CellStride) - 1);
            WeekdayGutter.Children.Add(label);
        }
    }

    /// <summary>Reads a themed brush by key. Resolved per call, never cached — a live theme flip
    /// must not leave these labels painted in the old theme's colour.</summary>
    private static Brush? Brush(string key)
        => Application.Current.Resources[key] as Brush;

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1000.0:0.#}K",
        _ => tokens.ToString(CultureInfo.CurrentCulture),
    };
}
