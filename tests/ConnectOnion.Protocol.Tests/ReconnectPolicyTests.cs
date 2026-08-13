namespace ConnectOnion.Protocol.Tests;

public class ReconnectPolicyTests
{
    [Fact]
    public void ShouldRetry_AllowsExactlyFiveAttempts()
    {
        Assert.False(ReconnectPolicy.ShouldRetry(0));
        for (var i = 1; i <= 5; i++) Assert.True(ReconnectPolicy.ShouldRetry(i));
        Assert.False(ReconnectPolicy.ShouldRetry(6));
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(4, 8000)]
    [InlineData(5, 16000)]
    public void DelayFor_DoublesAndStaysWithinJitterBand(int attempt, double nominalMs)
    {
        var policy = new ReconnectPolicy(seed: 1234);
        var delay = policy.DelayFor(attempt).TotalMilliseconds;

        // ±20% around the nominal 1/2/4/8/16s schedule.
        Assert.InRange(delay, nominalMs * 0.8, nominalMs * 1.2);
    }

    [Fact]
    public void DelayFor_RejectsAttemptsOutsideTheSchedule()
    {
        var policy = new ReconnectPolicy(seed: 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayFor(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayFor(6));
    }

    /// <summary>The whole point of jitter: two clients reconnecting off the same outage must
    /// not line up. Same attempt number, different processes → different delays.</summary>
    [Fact]
    public void DelayFor_JittersIndependentlyPerInstance()
    {
        var a = new ReconnectPolicy(seed: 1);
        var b = new ReconnectPolicy(seed: 2);

        var delaysA = Enumerable.Range(1, 5).Select(i => a.DelayFor(i)).ToArray();
        var delaysB = Enumerable.Range(1, 5).Select(i => b.DelayFor(i)).ToArray();

        Assert.NotEqual(delaysA, delaysB);
    }

    [Fact]
    public void DelayFor_IsNotConstantAcrossAttempts()
    {
        var policy = new ReconnectPolicy(seed: 99);
        // Jitter must never be large enough to reorder the schedule: 0.8 * 2^n > 1.2 * 2^(n-1)
        // holds for every step, so each attempt strictly exceeds the one before it.
        var previous = TimeSpan.Zero;
        for (var i = 1; i <= 5; i++)
        {
            var delay = policy.DelayFor(i);
            Assert.True(delay > previous, $"attempt {i} ({delay}) should exceed attempt {i - 1} ({previous})");
            previous = delay;
        }
    }
}
