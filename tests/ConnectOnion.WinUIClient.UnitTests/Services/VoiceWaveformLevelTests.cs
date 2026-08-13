using ConnectOnion.WinUIClient.Services.Speech;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class VoiceWaveformLevelTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.009)]
    public void FromRms_SilenceAndInvalidLevels_StayOnTheBaseline(double rms)
        => Assert.Equal(0, VoiceWaveformLevel.FromRms(rms));

    [Fact]
    public void FromRms_OrdinarySpeech_IsVisibleWithoutClipping()
    {
        var level = VoiceWaveformLevel.FromRms(0.04);

        Assert.InRange(level, 0.4, 0.6);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(1)]
    [InlineData(2)]
    public void FromRms_LoudAudio_IsClampedToFullHeight(double rms)
        => Assert.Equal(1, VoiceWaveformLevel.FromRms(rms));
}
