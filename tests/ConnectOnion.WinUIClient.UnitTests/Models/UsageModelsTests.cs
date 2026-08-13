using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class UsageModelsTests
{
    [Fact]
    public void UsageRecord_TotalTokens_ExcludesCacheAccountingFields()
    {
        var record = new UsageRecord("id", null, null, null, "model", 12, 8, 7, 3, null, 0);

        Assert.Equal(20, record.TotalTokens);
    }

    [Fact]
    public void ModelUsageSummary_CacheHitRatio_UsesCachedShareOfInput()
    {
        var summary = new ModelUsageSummary("model", 2, 40, 10, 30, 5, 100);

        Assert.Equal(50, summary.TotalTokens);
        Assert.Equal(0.75, summary.CacheHitRatio);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ModelUsageSummary_CacheHitRatio_WithNoPositiveInput_IsZero(long inputTokens)
    {
        var summary = new ModelUsageSummary("model", 1, inputTokens, 0, 10, 0, 0);

        Assert.Equal(0, summary.CacheHitRatio);
    }

    [Theory]
    [InlineData(UsageRange.Today, "Today")]
    [InlineData(UsageRange.Last7Days, "Last 7 days")]
    [InlineData(UsageRange.Last30Days, "Last 30 days")]
    [InlineData(UsageRange.AllTime, "All time")]
    public void Label_ReturnsUserFacingText(UsageRange range, string expected)
    {
        Assert.Equal(expected, range.Label());
    }

    [Fact]
    public void SinceUnixMs_AllTime_HasNoLowerBound()
    {
        Assert.Null(UsageRange.AllTime.SinceUnixMs());
    }

    [Theory]
    [InlineData(UsageRange.Last7Days, 7)]
    [InlineData(UsageRange.Last30Days, 30)]
    public void SinceUnixMs_RelativeRange_IsWithinCallWindow(UsageRange range, int days)
    {
        var earliest = DateTimeOffset.Now.AddDays(-days).ToUnixTimeMilliseconds();
        var actual = range.SinceUnixMs();
        var latest = DateTimeOffset.Now.AddDays(-days).ToUnixTimeMilliseconds();

        Assert.NotNull(actual);
        Assert.InRange(actual.Value, earliest, latest);
    }

    [Fact]
    public void SinceUnixMs_Today_ReturnsStartOfLocalDay()
    {
        var now = DateTimeOffset.Now;
        var expected = new DateTimeOffset(now.Date, now.Offset).ToUnixTimeMilliseconds();

        Assert.Equal(expected, UsageRange.Today.SinceUnixMs());
    }
}
