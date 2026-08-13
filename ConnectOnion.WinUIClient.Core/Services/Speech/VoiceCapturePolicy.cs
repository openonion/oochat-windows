using System;

namespace ConnectOnion.WinUIClient.Services.Speech;

/// <summary>Platform-free recording limits shared by the WinRT capture session and composer UI.</summary>
public static class VoiceCapturePolicy
{
    public const int SampleRate = 16_000;
    public const int MaxDurationSeconds = 120;
    public const long MaxSamples = (long)SampleRate * MaxDurationSeconds;
    public static TimeSpan MaxDuration { get; } = TimeSpan.FromSeconds(MaxDurationSeconds);

    public static bool ShouldFinish(TimeSpan elapsed) => elapsed >= MaxDuration;
}
