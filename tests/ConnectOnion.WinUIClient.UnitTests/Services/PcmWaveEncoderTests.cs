using System.Buffers.Binary;
using System.Text;
using ConnectOnion.WinUIClient.Services.Speech;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class PcmWaveEncoderTests
{
    [Fact]
    public void Encode_WritesA16KhzMonoPcmWaveHeaderAndPreservesSamples()
    {
        byte[] pcm = [0x01, 0x02, 0xFE, 0xFF];

        var wave = PcmWaveEncoder.Encode(pcm);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(4, 4)));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(wave, 8, 8));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(20, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(22, 2)));
        Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24, 4)));
        Assert.Equal(32_000, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(28, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(32, 2)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(34, 2)));
        Assert.Equal("data", Encoding.ASCII.GetString(wave, 36, 4));
        Assert.Equal(pcm.Length, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(40, 4)));
        Assert.Equal(pcm, wave[44..]);
    }

    [Fact]
    public void Encode_RejectsAnIncompletePcmFrame()
        => Assert.Throws<ArgumentException>(() => PcmWaveEncoder.Encode([0x01]));
}
