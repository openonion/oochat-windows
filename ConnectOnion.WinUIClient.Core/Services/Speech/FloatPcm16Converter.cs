using System.Buffers.Binary;

namespace ConnectOnion.WinUIClient.Services.Speech;

/// <summary>Converts AudioGraph float samples to little-endian PCM16 and measures their AC RMS.</summary>
public static class FloatPcm16Converter
{
    public static int GetRequiredByteCount(int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        return checked(sampleCount * sizeof(short));
    }

    public static double ConvertAndMeasureAcRms(
        ReadOnlySpan<float> samples,
        Span<byte> pcmDestination)
    {
        var requiredBytes = GetRequiredByteCount(samples.Length);
        if (pcmDestination.Length < requiredBytes)
        {
            throw new ArgumentException(
                "The PCM destination is too small for the supplied samples.",
                nameof(pcmDestination));
        }

        double sum = 0;
        double sumOfSquares = 0;
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = float.IsFinite(samples[index])
                ? Math.Clamp(samples[index], -1f, 1f)
                : 0;
            var pcm = (short)(sample < 0 ? sample * 32768 : sample * 32767);
            BinaryPrimitives.WriteInt16LittleEndian(
                pcmDestination.Slice(index * sizeof(short), sizeof(short)), pcm);

            sum += sample;
            sumOfSquares += sample * sample;
        }

        if (samples.IsEmpty) return 0;
        var mean = sum / samples.Length;
        var variance = Math.Max(0, sumOfSquares / samples.Length - mean * mean);
        return Math.Sqrt(variance);
    }
}
