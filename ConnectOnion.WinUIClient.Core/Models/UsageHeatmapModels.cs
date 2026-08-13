using System.Globalization;

namespace ConnectOnion.WinUIClient.Models;

/// <summary>One local calendar day's usage, as aggregated by
/// <c>UsageRepository.GetDailyTotalsAsync</c>. Only days with activity exist.</summary>
public sealed record DailyUsageTotal(DateOnly Date, long InputTokens, long OutputTokens, long Calls)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// One square in the heatmap.
/// </summary>
/// <param name="IsInRange">False for the padding squares that fill out the first and last weeks.
/// The grid is always whole weeks, but a square for a day outside the window must render as a
/// hole rather than as a zero-usage day — otherwise the map claims the user was idle on dates it
/// was never asked about.</param>
/// <param name="Level">Colour bucket, 0–4. 0 means "no usage", 1–4 are increasing intensity.</param>
public sealed record UsageHeatmapDay(
    DateOnly Date,
    long TotalTokens,
    long InputTokens,
    long OutputTokens,
    long Calls,
    int Level,
    bool IsInRange)
{
    /// <summary>Plain get-only members only — this type is bound from an <c>x:Bind</c>
    /// DataTemplate, which cannot use <c>required</c>/<c>init</c> accessors (see CLAUDE.md).</summary>
    public bool HasUsage => IsInRange && TotalTokens > 0;

    /// <summary>The tooltip and the screen-reader name are the same sentence on purpose: a
    /// keyboard user and a mouse user should be told exactly the same thing.</summary>
    public string Description => !IsInRange
        ? ""
        : TotalTokens == 0
            ? $"No token usage on {Date.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture)}"
            : $"{FormatTokens(TotalTokens)} tokens across {Calls} " +
              $"{(Calls == 1 ? "call" : "calls")} on " +
              $"{Date.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture)} " +
              $"({FormatTokens(InputTokens)} in, {FormatTokens(OutputTokens)} out)";

    internal static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1000.0:0.#}K",
        _ => tokens.ToString(CultureInfo.CurrentCulture),
    };
}

/// <summary>A month name and the grid column it starts at, for the labels along the top.</summary>
public sealed record UsageHeatmapMonth(string Label, int WeekIndex);

/// <summary>
/// A full year of daily token usage laid out as GitHub lays out contributions: one column per
/// week, one row per weekday, most recent week last.
/// </summary>
public sealed class UsageHeatmap
{
    /// <summary>Weeks start on Monday, so the row order is Mon…Sun and the Mon/Wed/Fri labels the
    /// design calls for land on rows 0, 2 and 4.</summary>
    public const int RowsPerWeek = 7;

    /// <summary>Number of colour buckets above zero. Level 0 is "no usage".</summary>
    public const int MaxLevel = 4;

    private UsageHeatmap(
        IReadOnlyList<UsageHeatmapDay> days,
        IReadOnlyList<UsageHeatmapMonth> months,
        int weekCount,
        DateOnly start,
        DateOnly end,
        long totalTokens,
        long maxDailyTokens,
        int activeDayCount)
    {
        Days = days;
        Months = months;
        WeekCount = weekCount;
        Start = start;
        End = end;
        TotalTokens = totalTokens;
        MaxDailyTokens = maxDailyTokens;
        ActiveDayCount = activeDayCount;
    }

    /// <summary>
    /// Every square, ordered <b>column-major</b>: the seven days of the first week, then the seven
    /// of the next. That order is what lets the view hand this straight to a
    /// <c>UniformGridLayout</c> with <c>Orientation="Vertical"</c> and get GitHub's layout without
    /// building a nested weeks-of-days structure.
    /// </summary>
    public IReadOnlyList<UsageHeatmapDay> Days { get; }

    public IReadOnlyList<UsageHeatmapMonth> Months { get; }

    public int WeekCount { get; }

    /// <summary>First day of the first (padded) week.</summary>
    public DateOnly Start { get; }

    /// <summary>The last day with data — normally today.</summary>
    public DateOnly End { get; }

    public long TotalTokens { get; }

    public long MaxDailyTokens { get; }

    /// <summary>Days in range that had any usage. Drives the "N active days" summary.</summary>
    public int ActiveDayCount { get; }

    public bool IsEmpty => TotalTokens == 0;

    /// <summary>
    /// Builds the grid for the twelve months ending on <paramref name="endDate"/>.
    /// </summary>
    /// <param name="totals">Days with usage, in any order. Days outside the window are ignored;
    /// duplicates for the same date are summed, so a caller need not pre-group.</param>
    /// <param name="endDate">Last day shown — the caller passes today. Taken as a parameter rather
    /// than read from the clock so the whole projection is deterministic under test.</param>
    /// <param name="months">Length of the window in months. 12 is the design; the parameter exists
    /// so a test can build a small grid without fabricating a year of dates.</param>
    public static UsageHeatmap Build(
        IEnumerable<DailyUsageTotal> totals, DateOnly endDate, int months = 12)
    {
        // Rolling window, not a calendar year: the design asks for "the last 12 months ending
        // today", so the map always ends in the current week rather than collapsing every January.
        var firstDay = endDate.AddMonths(-months).AddDays(1);

        // Pad backwards to Monday so every column is a whole week and row 0 is always Monday.
        // DayOfWeek treats Sunday as 0, hence the +6 %7 rather than a plain subtraction.
        var start = firstDay.AddDays(-(((int)firstDay.DayOfWeek + 6) % 7));
        // Pad forwards to Sunday for the same reason — the final column must be full width even
        // when today is a Wednesday.
        var end = endDate.AddDays(6 - (((int)endDate.DayOfWeek + 6) % 7));

        var byDate = new Dictionary<DateOnly, DailyUsageTotal>();
        foreach (var total in totals)
        {
            if (total.Date < firstDay || total.Date > endDate) continue;
            byDate[total.Date] = byDate.TryGetValue(total.Date, out var existing)
                ? existing with
                {
                    InputTokens = existing.InputTokens + total.InputTokens,
                    OutputTokens = existing.OutputTokens + total.OutputTokens,
                    Calls = existing.Calls + total.Calls,
                }
                : total;
        }

        var thresholds = ComputeThresholds(byDate.Values.Select(v => v.TotalTokens));

        var days = new List<UsageHeatmapDay>();
        var months_ = new List<UsageHeatmapMonth>();
        var weekCount = 0;
        var lastMonthLabelled = -1;

        for (var cursor = start; cursor <= end; cursor = cursor.AddDays(1))
        {
            var inRange = cursor >= firstDay && cursor <= endDate;
            byDate.TryGetValue(cursor, out var total);

            days.Add(new UsageHeatmapDay(
                Date: cursor,
                TotalTokens: total?.TotalTokens ?? 0,
                InputTokens: total?.InputTokens ?? 0,
                OutputTokens: total?.OutputTokens ?? 0,
                Calls: total?.Calls ?? 0,
                Level: inRange ? LevelFor(total?.TotalTokens ?? 0, thresholds) : 0,
                IsInRange: inRange));

            // A column is complete on Sunday; label the month at the top of the column that
            // contains the first day of a new month, which is what puts "Jul" above the week
            // July actually starts in rather than above the week it ends in.
            if (cursor.DayOfWeek == DayOfWeek.Monday)
            {
                if (inRange && cursor.Month != lastMonthLabelled)
                {
                    lastMonthLabelled = cursor.Month;
                    months_.Add(new UsageHeatmapMonth(
                        cursor.ToString("MMM", CultureInfo.CurrentCulture), weekCount));
                }

                weekCount++;
            }
        }

        // The loop counts a week when it sees its Monday, so the final partial column — there
        // isn't one, since `end` is a Sunday — is already included. Guard anyway: a zero-length
        // window must not produce a zero-column grid the view then divides by.
        weekCount = Math.Max(weekCount, 1);

        var inRangeDays = days.Where(d => d.IsInRange).ToList();

        return new UsageHeatmap(
            days,
            months_,
            weekCount,
            start,
            endDate,
            inRangeDays.Sum(d => d.TotalTokens),
            inRangeDays.Count == 0 ? 0 : inRangeDays.Max(d => d.TotalTokens),
            inRangeDays.Count(d => d.TotalTokens > 0));
    }

    /// <summary>
    /// Bucket boundaries from the quartiles of the <i>non-zero</i> days.
    ///
    /// <para>Deliberately not <c>max/4</c>: token usage is extremely long-tailed — one day spent
    /// on a long agent session can be a hundred times a normal day — and linear scaling against
    /// the max would render every ordinary day as level 1 and the map as a single bright square
    /// on an empty field. Ranking against the distribution keeps the map readable whatever the
    /// user's scale, which is the same reason GitHub buckets by rank rather than by count.</para>
    /// </summary>
    private static long[] ComputeThresholds(IEnumerable<long> nonZeroTotals)
    {
        var sorted = nonZeroTotals.Where(t => t > 0).OrderBy(t => t).ToArray();
        if (sorted.Length == 0) return [];

        // Quartile cut points. With very few active days these collapse onto each other, which is
        // correct: three active days should not be spread across four visual intensities as if
        // the difference were meaningful.
        return
        [
            Quantile(sorted, 0.25),
            Quantile(sorted, 0.50),
            Quantile(sorted, 0.75),
        ];
    }

    private static long Quantile(long[] sorted, double q)
    {
        var index = (int)Math.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>Maps a day's tokens onto 0–4. Any usage at all is at least level 1, so a quiet day
    /// is still visibly distinct from a day with nothing on it.</summary>
    private static int LevelFor(long tokens, long[] thresholds)
    {
        if (tokens <= 0) return 0;
        if (thresholds.Length == 0) return 1;

        var level = 1;
        foreach (var threshold in thresholds)
        {
            if (tokens > threshold) level++;
        }

        return Math.Min(level, MaxLevel);
    }
}
