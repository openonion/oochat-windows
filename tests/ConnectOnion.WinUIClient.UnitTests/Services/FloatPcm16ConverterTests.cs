using System.Buffers.Binary;
using ConnectOnion.WinUIClient.Services.Speech;

namespace ConnectOnion.WinUIClient.UnitTests.Services;

public sealed class FloatPcm16ConverterTests
{
    [Fact]
    public void ConvertAndMeasureAcRms_ConvertsFloatSamplesToLittleEndianPcm16()
    {
        float[] samples = [-1, -0.5f, 0, 0.5f, 1, float.NaN, float.PositiveInfinity];
        var pcm = new byte[FloatPcm16Converter.GetRequiredByteCount(samples.Length)];

        FloatPcm16Converter.ConvertAndMeasureAcRms(samples, pcm);

        Assert.Equal(
            new short[] { short.MinValue, -16384, 0, 16383, short.MaxValue, 0, 0 },
            Enumerable.Range(0, samples.Length)
                .Select(index => BinaryPrimitives.ReadInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)))));
    }

    [Fact]
    public void ConvertAndMeasureAcRms_RemovesDcBiasFromTheWaveformLevel()
    {
        float[] samples = [0.25f, 0.25f, 0.25f, 0.25f];
        var pcm = new byte[FloatPcm16Converter.GetRequiredByteCount(samples.Length)];

        var rms = FloatPcm16Converter.ConvertAndMeasureAcRms(samples, pcm);

        Assert.Equal(0, rms);
    }

    [Fact]
    public void ConvertAndMeasureAcRms_MeasuresTheConvertedAcSignal()
    {
        float[] samples = [-0.5f, 0.5f, -0.5f, 0.5f];
        var pcm = new byte[FloatPcm16Converter.GetRequiredByteCount(samples.Length)];

        var rms = FloatPcm16Converter.ConvertAndMeasureAcRms(samples, pcm);

        Assert.Equal(0.5, rms, precision: 6);
    }

    [Fact]
    public void ConvertAndMeasureAcRms_RejectsAnUndersizedDestination()
    {
        Assert.Throws<ArgumentException>(() =>
            FloatPcm16Converter.ConvertAndMeasureAcRms([0, 1], new byte[2]));
    }
}
