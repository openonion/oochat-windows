namespace ConnectOnion.WinUIClient.Services.Speech;

/// <summary>Maps a PCM RMS level to a stable 0..1 waveform height.</summary>
public static class VoiceWaveformLevel
{
    // Shared-mode microphone processing commonly leaves a small noise floor even in a quiet room.
    // Keep levels at or below -40 dBFS on the baseline, then use a logarithmic range so ordinary
    // speech remains visible without multiplying that floor into a full-height bar.
    private const double SilenceFloorDb = -40;
    private const double FullHeightDb = -16;

    public static double FromRms(double rms)
    {
        if (!double.IsFinite(rms) || rms <= 0) return 0;

        var decibels = 20 * Math.Log10(Math.Min(rms, 1));
        return Math.Clamp(
            (decibels - SilenceFloorDb) / (FullHeightDb - SilenceFloorDb),
            0,
            1);
    }
}
