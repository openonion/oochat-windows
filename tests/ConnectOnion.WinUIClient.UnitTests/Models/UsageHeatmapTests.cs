using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class UsageHeatmapTests
{
    private static readonly DateOnly Today = new(2026, 7, 20); // a Monday

    private static DailyUsageTotal Day(DateOnly date, long tokens, long calls = 1) =>
        new(date, InputTokens: tokens / 2, OutputTokens: tokens - tokens / 2, Calls: calls);

    [Fact]
    public void Build_GridIsWholeWeeks_StartingMondayEndingSunday()
    {
        var map = UsageHeatmap.Build([], Today);

        Assert.Equal(DayOfWeek.Monday, map.Start.DayOfWeek);
        Assert.Equal(0, map.Days.Count % UsageHeatmap.RowsPerWeek);
        Assert.Equal(map.WeekCount * UsageHeatmap.RowsPerWeek, map.Days.Count);
    }

    [Fact]
    public void Build_DaysAreColumnMajor_SoTheViewCanFlowThemVertically()
    {
        // The view hands Days straight to a UniformGridLayout with 7 rows; if this order ever
        // changed to row-major the grid would silently transpose.
        var map = UsageHeatmap.Build([], Today);

        for (var i = 0; i < UsageHeatmap.RowsPerWeek; i++)
        {
            Assert.Equal(map.Start.AddDays(i), map.Days[i].Date);
        }

        // First square of the second column is the next Monday.
        Assert.Equal(map.Start.AddDays(7), map.Days[UsageHeatmap.RowsPerWeek].Date);
    }

    [Fact]
    public void Build_WindowIsTrailingTwelveMonths_NotACalendarYear()
    {
        var map = UsageHeatmap.Build([], Today);

        Assert.Equal(Today, map.End);
        // Padding may push Start earlier, but never more than a week before the window opens.
        var windowStart = Today.AddMonths(-12).AddDays(1);
        Assert.True(map.Start <= windowStart);
        Assert.True(windowStart.DayNumber - map.Start.DayNumber < 7);
    }

    [Fact]
    public void Build_PadsOutOfRangeDays_AsHolesNotZeroUsageDays()
    {
        var map = UsageHeatmap.Build([], Today);

        // Squares after today exist (the last column is padded to Sunday) but must not claim the
        // user was idle on dates the map was never asked about.
        var future = map.Days.Where(d => d.Date > Today).ToList();
        Assert.NotEmpty(future);
        Assert.All(future, d => Assert.False(d.IsInRange));
        Assert.All(future, d => Assert.Equal("", d.Description));
    }

    [Fact]
    public void Build_IgnoresDaysOutsideTheWindow()
    {
        var stale = Today.AddYears(-2);
        var map = UsageHeatmap.Build([Day(stale, 5_000), Day(Today, 1_000)], Today);

        Assert.Equal(1_000, map.TotalTokens);
        Assert.Equal(1, map.ActiveDayCount);
    }

    [Fact]
    public void Build_SumsDuplicateDates_SoCallersNeedNotPreGroup()
    {
        var map = UsageHeatmap.Build([Day(Today, 100, calls: 2), Day(Today, 400, calls: 3)], Today);

        var today = map.Days.Single(d => d.Date == Today);
        Assert.Equal(500, today.TotalTokens);
        Assert.Equal(5, today.Calls);
        Assert.Equal(1, map.ActiveDayCount);
    }

    [Fact]
    public void Build_AnyUsageIsAtLeastLevelOne_AndNoUsageIsLevelZero()
    {
        // A quiet day must still read as distinct from a day with nothing on it.
        var map = UsageHeatmap.Build([Day(Today, 1)], Today);

        Assert.Equal(1, map.Days.Single(d => d.Date == Today).Level);
        Assert.Equal(0, map.Days.Single(d => d.Date == Today.AddDays(-1)).Level);
    }

    [Fact]
    public void Build_LevelsRankAgainstDistribution_NotAgainstTheMaximum()
    {
        // The point of quartile bucketing: one enormous day must not flatten every ordinary day
        // to the lowest level. With max/4 scaling, all four small days below would be level 1.
        var totals = new List<DailyUsageTotal>
        {
            Day(Today.AddDays(-4), 100),
            Day(Today.AddDays(-3), 200),
            Day(Today.AddDays(-2), 300),
            Day(Today.AddDays(-1), 400),
            Day(Today, 1_000_000),
        };

        var map = UsageHeatmap.Build(totals, Today);
        var levels = totals
            .Select(t => map.Days.Single(d => d.Date == t.Date).Level)
            .ToArray();

        Assert.Equal(UsageHeatmap.MaxLevel, levels[^1]);
        // The ordinary days spread across more than one level rather than all collapsing to 1.
        Assert.True(levels[..^1].Distinct().Count() > 1, $"levels were [{string.Join(",", levels)}]");
        // Ranking is monotonic: more tokens never means a lower level.
        for (var i = 1; i < levels.Length; i++) Assert.True(levels[i] >= levels[i - 1]);
    }

    [Fact]
    public void Build_LevelsNeverExceedMax()
    {
        var totals = Enumerable.Range(0, 60)
            .Select(i => Day(Today.AddDays(-i), (i + 1) * 1_000L))
            .ToList();

        var map = UsageHeatmap.Build(totals, Today);

        Assert.All(map.Days, d => Assert.InRange(d.Level, 0, UsageHeatmap.MaxLevel));
    }

    [Fact]
    public void Build_MonthLabels_AreInColumnOrderAndNotRepeated()
    {
        var map = UsageHeatmap.Build([], Today);

        Assert.NotEmpty(map.Months);
        // Twelve months of columns yields 12 or 13 labels depending on where the window opens.
        Assert.InRange(map.Months.Count, 12, 13);
        Assert.Equal(map.Months.OrderBy(m => m.WeekIndex).Select(m => m.WeekIndex),
                     map.Months.Select(m => m.WeekIndex));
        Assert.Equal(map.Months.Select(m => m.WeekIndex).Distinct().Count(), map.Months.Count);
        Assert.All(map.Months, m => Assert.InRange(m.WeekIndex, 0, map.WeekCount - 1));
    }

    [Fact]
    public void Build_EmptyLedger_IsEmptyButStillAFullGrid()
    {
        var map = UsageHeatmap.Build([], Today);

        Assert.True(map.IsEmpty);
        Assert.Equal(0, map.MaxDailyTokens);
        Assert.Equal(0, map.ActiveDayCount);
        Assert.True(map.WeekCount >= 52);
        Assert.All(map.Days, d => Assert.Equal(0, d.Level));
    }

    [Fact]
    public void Description_ReadsAsOneSentence_ForUsedAndUnusedDays()
    {
        var map = UsageHeatmap.Build([Day(Today, 2_500, calls: 1)], Today);

        var used = map.Days.Single(d => d.Date == Today);
        Assert.Contains("2.5K tokens", used.Description, StringComparison.Ordinal);
        Assert.Contains("1 call", used.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("1 calls", used.Description, StringComparison.Ordinal);
        Assert.True(used.HasUsage);

        var unused = map.Days.Single(d => d.Date == Today.AddDays(-1));
        Assert.StartsWith("No token usage", unused.Description, StringComparison.Ordinal);
        Assert.False(unused.HasUsage);
    }

    [Theory]
    [InlineData(2026, 7, 20)]  // Monday
    [InlineData(2026, 7, 25)]  // Saturday
    [InlineData(2026, 7, 26)]  // Sunday — the boundary that off-by-one week maths gets wrong
    [InlineData(2026, 1, 1)]   // year boundary
    [InlineData(2024, 2, 29)]  // leap day
    public void Build_AnyEndDate_ProducesAWholeWeekGridContainingThatDay(int y, int m, int d)
    {
        var end = new DateOnly(y, m, d);

        var map = UsageHeatmap.Build([], end);

        Assert.Equal(DayOfWeek.Monday, map.Start.DayOfWeek);
        Assert.Equal(0, map.Days.Count % UsageHeatmap.RowsPerWeek);
        Assert.Contains(map.Days, day => day.Date == end && day.IsInRange);
    }
}
