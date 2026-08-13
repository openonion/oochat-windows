using ConnectOnion.WinUIClient.Services.Speech;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class VoiceCapturePolicyTests
{
    [Fact]
    public void Limits_AreInternallyConsistent()
    {
        Assert.Equal(16_000, VoiceCapturePolicy.SampleRate);
        Assert.Equal(TimeSpan.FromMinutes(2), VoiceCapturePolicy.MaxDuration);
        Assert.Equal(
            VoiceCapturePolicy.SampleRate * (long)VoiceCapturePolicy.MaxDurationSeconds,
            VoiceCapturePolicy.MaxSamples);
    }

    [Theory]
    [InlineData(119_999, false)]
    [InlineData(120_000, true)]
    [InlineData(120_001, true)]
    public void ShouldFinish_EnforcesTheBoundary(int elapsedMilliseconds, bool expected)
    {
        Assert.Equal(
            expected,
            VoiceCapturePolicy.ShouldFinish(TimeSpan.FromMilliseconds(elapsedMilliseconds)));
    }
}
