using ConnectOnion.WinUIClient.Services.Speech;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class VoiceActivityDetectorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.0119)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void IsVoicedFrame_RejectsNoiseAndInvalidLevels(double rms)
        => Assert.False(VoiceActivityDetector.IsVoicedFrame(rms));

    [Theory]
    [InlineData(0.012)]
    [InlineData(0.04)]
    public void IsVoicedFrame_AcceptsSpeechLevelAudio(double rms)
        => Assert.True(VoiceActivityDetector.IsVoicedFrame(rms));

    [Fact]
    public void HasSufficientSpeech_RequiresAQuarterSecondOfVoicedSamples()
    {
        Assert.False(VoiceActivityDetector.HasSufficientSpeech(3_999, 16_000));
        Assert.True(VoiceActivityDetector.HasSufficientSpeech(4_000, 16_000));
    }
}
