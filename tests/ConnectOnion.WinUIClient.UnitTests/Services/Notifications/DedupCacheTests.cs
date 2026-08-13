using ConnectOnion.WinUIClient.Services.Notifications;

namespace ConnectOnion.WinUIClient.UnitTests.Services.Notifications;

public sealed class DedupCacheTests
{
    [Fact]
    public void Add_DuplicateKey_ReturnsFalseWithoutAddingTwice()
    {
        var cache = new DedupCache();

        Assert.True(cache.Add("event-1"));
        Assert.False(cache.Add("event-1"));
        Assert.True(cache.Contains("event-1"));
    }

    [Fact]
    public void Add_CapacityExceeded_EvictsOldestKey()
    {
        var cache = new DedupCache(capacity: 2);

        cache.Add("event-1");
        cache.Add("event-2");
        cache.Add("event-3");

        Assert.False(cache.Contains("event-1"));
        Assert.True(cache.Contains("event-2"));
        Assert.True(cache.Contains("event-3"));
    }

    [Fact]
    public void Constructor_NonPositiveCapacity_UsesMinimumCapacityOfOne()
    {
        var cache = new DedupCache(capacity: 0);

        cache.Add("event-1");
        cache.Add("event-2");

        Assert.False(cache.Contains("event-1"));
        Assert.True(cache.Contains("event-2"));
    }
}
