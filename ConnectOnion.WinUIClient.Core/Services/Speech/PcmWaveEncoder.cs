using System.Buffers.Binary;

namespace ConnectOnion.WinUIClient.Services.Speech;

/// <summary>Builds the small PCM WAV payload accepted by the OpenOnion transcription endpoint.</summary>
public static class PcmWaveEncoder
{
    private const int HeaderSize = 44;

    public static byte[] Encode(
        ReadOnlySpan<byte> pcm,
        int sampleRate = 16_000,
        short channelCount = 1,
        short bitsPerSample = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        if (bitsPerSample <= 0 || bitsPerSample % 8 != 0)
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample));

        var blockAlign = checked((short)(channelCount * (bitsPerSample / 8)));
        if (pcm.Length % blockAlign != 0)
            throw new ArgumentException("PCM data must contain complete sample frames.", nameof(pcm));

        var result = GC.AllocateUninitializedArray<byte>(checked(HeaderSize + pcm.Length));
        var header = result.AsSpan(0, HeaderSize);
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], checked(36 + pcm.Length));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1); // Linear PCM.
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], channelCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(
            header[28..], checked(sampleRate * blockAlign));
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], bitsPerSample);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], pcm.Length);
        pcm.CopyTo(result.AsSpan(HeaderSize));
        return result;
    }
}
