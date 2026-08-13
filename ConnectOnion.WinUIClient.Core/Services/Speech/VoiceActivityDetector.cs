namespace ConnectOnion.WinUIClient.Services.Speech;

/// <summary>Small local gate that prevents silence-only recordings from reaching the paid API.</summary>
public static class VoiceActivityDetector
{
    // About -38.4 dBFS: above the waveform's visual noise floor but still below normal speech.
    public const double SpeechRmsThreshold = 0.012;
    public const double MinimumVoicedSeconds = 0.25;

    public static bool IsVoicedFrame(double rms)
        => double.IsFinite(rms) && rms >= SpeechRmsThreshold;

    public static bool HasSufficientSpeech(long voicedSamples, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(voicedSamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        return voicedSamples >= Math.Ceiling(sampleRate * MinimumVoicedSeconds);
    }
}
