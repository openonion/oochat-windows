using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ConnectOnion.WinUIClient.Services;
using ConnectOnion.WinUIClient.Services.Speech;
using ConnectOnion.WinUIClient.Common;
using Microsoft.Extensions.Logging;

namespace ConnectOnion.WinUIClient.Controls;

// Voice input for the composer: microphone capture, the OpenOnion transcription request,
// and the lightweight XAML scrolling waveform. Split out of
// ChatComposer.xaml.cs so that file stays focused on text/attachments/submit.
// The constructor wiring (ApplyWaveformTheme, _waveformTimer.Tick,
// _recordingElapsedTimer.Tick, ActualThemeChanged) and RefreshSendButtonState()
// live in ChatComposer.xaml.cs; both partials share this one ChatComposer class.
public sealed partial class ChatComposer
{
    // ---- Speech diagnostics ----
    //
    // Capture depends on device/privacy state and transcription depends on the network/service.
    // Both degrade back to the text composer, so log the concrete failure behind the actionable
    // inline message the user sees.
    //
    // Warning rather than Error throughout: none of these stop the app, and the text composer is
    // always still there. LoggerMessage.Define because CA1848 is on. The `Log` property lives in
    // ChatComposer.xaml.cs 鈥?this is the same partial class. EventIds continue that file's
    // sequence, which ends at 3.

    private static readonly Action<ILogger, string, Exception?> LogVoiceCaptureUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(10, "VoiceCaptureUnavailable"),
            "Voice capture could not start ({Reason}); the composer stays text-only");

    private static readonly Action<ILogger, string, Exception?> LogVoiceTranscriptionFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(11, "VoiceTranscriptionFailed"),
            "Voice transcription failed ({Reason}); the recorded audio was not retained");

    private static readonly Action<ILogger, Exception?> LogVoiceConsentUnavailable =
        LoggerMessage.Define(LogLevel.Warning, new EventId(17, "VoiceConsentUnavailable"),
            "Voice transcription consent could not be loaded or saved; recording was not started");

    /// <summary>Information, not Warning: this is the healthy-path footprint trace described on
    /// <see cref="LogSpeechFootprint"/>, not a failure.</summary>
    private static readonly Action<ILogger, string, long, long, Exception?> LogSpeechMemoryFootprint =
        LoggerMessage.Define<string, long, long>(LogLevel.Information, new EventId(16, "SpeechMemoryFootprint"),
            "Speech {Phase}: working set {WorkingSetMb} MB, managed heap {HeapKb} KB");

    // ---- Waveform constants ----
    private const float BarWidth = 2;
    private const float BarGap = 1;
    private const float SampleStride = BarWidth + BarGap;
    private const int MaxRingSamples = 600;
    private const double SmoothingFactor = 0.35;

    // ---- Recording state machine ----
    //
    // Starting the capture device and remotely transcribing both take real time. Explicit states
    // keep re-entrant clicks out and let the same action button stop recording or cancel a request.
    private enum RecordingState { Idle, Starting, Recording, Transcribing }
    private RecordingState _recordingState = RecordingState.Idle;
    private static readonly TimeSpan MaxVoiceDuration = VoiceCapturePolicy.MaxDuration;
    /// <summary>Cancels microphone startup or an in-flight transcription request.</summary>
    private CancellationTokenSource? _recordingCts;

    private readonly VoiceCaptureSession _voiceCapture = new();
    private VoiceCaptureStartFailure _audioCaptureFailure;
    private string _preferredMicrophoneDeviceId = "";

    private string _voiceDraftAtStart = "";
    private int _voiceSelectionStart;
    private int _voiceSelectionLength;

    private readonly DispatcherTimer _recordingElapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _waveformTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private double _smoothedAmplitude;
    private DateTimeOffset _recordingStartedAt;

    // ---- Waveform ring buffer ----
    // Fixed-size and overwriting: the waveform scrolls, so only the most recent MaxRingSamples
    // are ever drawn and an unbounded list would grow for the whole recording to no purpose.
    // _sampleCount saturates at capacity and exists to distinguish a partially-filled buffer
    // (draw only what we have) from a wrapped one (draw everything, oldest first).
    private readonly double[] _sampleRing = new double[MaxRingSamples];
    private int _sampleWritePos;
    private int _sampleCount;

    private readonly System.Collections.Generic.List<Rectangle> _waveformBars = new();
    private readonly SolidColorBrush _waveformBrush = new();

    private bool IsVoiceInputActive => _recordingState != RecordingState.Idle;

    private async void Speech_Click(object sender, RoutedEventArgs e)
    {
        if (IsVoiceInputActive || !CanSubmit) return;

        if (!await EnsureVoiceCloudConsentAsync())
        {
            FocusInput();
            return;
        }

        _recordingCts?.Cancel();
        _recordingCts?.Dispose();
        _recordingCts = new CancellationTokenSource();
        var ct = _recordingCts.Token;
        _voiceDraftAtStart = InputBox.Text;
        _voiceSelectionStart = InputBox.SelectionStart;
        _voiceSelectionLength = InputBox.SelectionLength;
        CloseSkillPalette();
        _recordingState = RecordingState.Starting;
        ResetRecordingVisuals();
        ClearComposerError();
        RefreshSendButtonState();
        CancelSpeechButton.Focus(FocusState.Programmatic);
        LogSpeechFootprint("starting");

        try
        {
            _audioCaptureFailure = VoiceCaptureStartFailure.None;
            await StartAudioMeterAsync(ct);

            if (_audioCaptureFailure != VoiceCaptureStartFailure.None)
            {
                _recordingState = RecordingState.Idle;
                RefreshSendButtonState();
                StopAudioMeter(discardAudio: true);
                ShowVoiceCaptureFailure(_audioCaptureFailure);
                FocusInput();
                return;
            }

            ct.ThrowIfCancellationRequested();

            _recordingState = RecordingState.Recording;
            StartRecordingVisuals();
            RefreshSendButtonState();
            FinishSpeechButton.Focus(FocusState.Programmatic);
        }
        catch (OperationCanceledException)
        {
            StopAudioMeter(discardAudio: true);
            _recordingState = RecordingState.Idle;
            if (_disposed == 0) RefreshSendButtonState();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            LogVoiceCaptureUnavailable(Log, ex.GetType().Name, ex);
            _recordingState = RecordingState.Idle;
            StopAudioMeter(discardAudio: true);
            if (_disposed == 0)
            {
                RefreshSendButtonState();
                ShowVoiceCaptureFailure(ex is UnauthorizedAccessException
                    ? VoiceCaptureStartFailure.AccessDenied
                    : VoiceCaptureStartFailure.Unavailable);
                FocusInput();
            }
        }
    }

    private async void FinishSpeech_Click(object sender, RoutedEventArgs e)
    {
        await FinishVoiceInputAsync();
    }

    private void CancelSpeech_Click(object sender, RoutedEventArgs e) => CancelVoiceInput();

    private async System.Threading.Tasks.Task FinishVoiceInputAsync()
    {
        if (_recordingState != RecordingState.Recording || _recordingCts is null) return;

        var session = _recordingCts;
        var ct = session.Token;
        _recordingState = RecordingState.Transcribing;
        StopRecordingVisuals();
        StopAudioMeter();
        var hasSpeech = _voiceCapture.HasCapturedSpeech();
        var waveAudio = _voiceCapture.TakeWaveAudio();
        RefreshSendButtonState();
        CancelSpeechButton.Focus(FocusState.Programmatic);

        try
        {
            if (!hasSpeech || waveAudio.Length <= 44)
            {
                ShowComposerError(LocalizedStrings.Get(
                    "VoiceNoSpeech", "No speech was detected. Try again or type your message."));
                return;
            }

            var transcript = await AppServices.VoiceTranscription.TranscribeAsync(waveAudio, ct);
            ct.ThrowIfCancellationRequested();
            AppendVoiceTranscript(transcript);
            ClearComposerError();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user cancelled transcription or the composer was unloaded.
        }
        catch (Exception) when (_disposed != 0)
        {
            // Window teardown cancelled the owner; never resume into its visual tree.
        }
        catch (OperationCanceledException ex)
        {
            LogVoiceTranscriptionFailed(Log, "Timeout", ex);
            ShowComposerError(LocalizedStrings.Get(
                "VoiceTranscriptionTimeout",
                "Voice transcription timed out. Check your connection and try again."));
        }
        catch (HttpRequestException ex)
        {
            LogVoiceTranscriptionFailed(Log, ex.GetType().Name, ex);
            ShowComposerError(LocalizedStrings.Get(
                "VoiceTranscriptionNetworkUnavailable",
                "Voice transcription could not be reached. Check your connection and try again."));
        }
        catch (VoiceTranscriptionException ex)
        {
            LogVoiceTranscriptionFailed(Log, ex.Failure.ToString(), ex);
            ShowComposerError(VoiceTranscriptionFailureMessage(ex.Failure));
        }
        finally
        {
            if (ReferenceEquals(_recordingCts, session))
            {
                _recordingState = RecordingState.Idle;
                if (_disposed == 0)
                {
                    RefreshSendButtonState();
                    FocusInput();
                }
                LogSpeechFootprint("stopped");
            }
        }
    }

    private async Task<bool> EnsureVoiceCloudConsentAsync()
    {
        try
        {
            var preferences = await AppServices.Preferences.LoadAsync(_lifetimeCts.Token);
            _preferredMicrophoneDeviceId = preferences.MicrophoneDeviceId ?? "";
            if (preferences.VoiceCloudTranscriptionConsent) return true;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = LocalizedStrings.Get(
                    "VoiceCloudConsentTitle", "Send audio for cloud transcription?"),
                Content = LocalizedStrings.Get(
                    "VoiceCloudConsentBody",
                    "Your recording will be sent securely to OpenOnion to create a transcript. "
                    + "ConnectOnion Desktop discards the audio after transcription and does not add it to chat history. "
                    + "OpenOnion processes it under its privacy policy."),
                PrimaryButtonText = LocalizedStrings.Get("VoiceCloudConsentContinue", "Continue"),
                CloseButtonText = LocalizedStrings.Get("CommonCancel", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            AutomationProperties.SetAutomationId(dialog, "VoiceCloudConsentDialog");

            if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary) return false;

            preferences.VoiceCloudTranscriptionConsent = true;
            await AppServices.Preferences.SaveAsync(preferences, _lifetimeCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogVoiceConsentUnavailable(Log, ex);
            ShowComposerError(LocalizedStrings.Get(
                "VoiceConsentSaveFailed",
                "Voice recording could not start because your consent preference could not be saved. Try again."));
            return false;
        }
    }

    private void CancelVoiceInput()
    {
        if (!IsVoiceInputActive) return;

        _recordingCts?.Cancel();
        StopRecordingVisuals();
        StopAudioMeter(discardAudio: true);
        _recordingState = RecordingState.Idle;
        RefreshSendButtonState();
        if (_disposed == 0) FocusInput();
    }

    private void AppendVoiceTranscript(string transcript)
    {
        var draftUnchanged = string.Equals(
            InputBox.Text, _voiceDraftAtStart, StringComparison.Ordinal);
        var insertion = VoiceTranscript.Insert(
            InputBox.Text,
            transcript,
            draftUnchanged ? _voiceSelectionStart : InputBox.SelectionStart,
            draftUnchanged ? _voiceSelectionLength : InputBox.SelectionLength);
        if (string.Equals(insertion.Text, InputBox.Text, StringComparison.Ordinal)) return;

        InputBox.Text = insertion.Text;
        InputBox.SelectionStart = insertion.CaretPosition;
        InputBox.SelectionLength = 0;
        RefreshSendButtonState();
    }

    private static string VoiceTranscriptionFailureMessage(VoiceTranscriptionFailure failure)
        => failure switch
        {
            VoiceTranscriptionFailure.Authentication => LocalizedStrings.Get(
                "VoiceAuthenticationFailure",
                "Voice transcription could not authenticate this identity. Try again or check Settings > Identity."),
            VoiceTranscriptionFailure.NoSpeech => LocalizedStrings.Get(
                "VoiceNoSpeech",
                "No speech was detected. Try again or type your message."),
            _ => LocalizedStrings.Get(
                "VoiceServiceUnavailable",
                "Voice transcription is unavailable right now. Check your connection and try again."),
        };

    private void ShowVoiceCaptureFailure(VoiceCaptureStartFailure failure)
    {
        var message = failure switch
        {
            VoiceCaptureStartFailure.AccessDenied => LocalizedStrings.Get(
                "VoiceMicrophoneBlocked",
                "Microphone access is blocked. Allow it in Windows Settings > Privacy & security > Microphone, then try again."),
            VoiceCaptureStartFailure.NoDevice => LocalizedStrings.Get(
                "VoiceMicrophoneMissing",
                "No microphone was found. Connect one, choose it in Settings, or type your message instead."),
            _ => LocalizedStrings.Get(
                "VoiceMicrophoneUnavailable",
                "The microphone is unavailable. Close other apps using it, check Settings, and try again."),
        };
        ShowComposerError(message);
    }

    /// <summary>
    /// Records the process footprint at each speech transition.
    ///
    /// <para>Here because "memory goes up every time I use the microphone" is otherwise
    /// unfalsifiable: the speech and audio stacks load native DLLs and buffers on first use and
    /// .NET does not hand working set back to the OS promptly, so some rise is expected and only
    /// a per-session trend distinguishes that from a leak. Two lines per recording is low enough
    /// volume to keep permanently, and turns the question into a number that can be read out of
    /// the log after a few clicks.</para>
    ///
    /// <para>Managed heap is the diagnostic half: a rising working set with a flat heap is the
    /// native stacks warming up, whereas a heap that climbs per session is a real managed leak.</para>
    /// </summary>
    private static void LogSpeechFootprint(string phase)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            process.Refresh();
            // Was a direct Serilog.Log.Information call. Routed through the same ILogger as
            // everything else here so this file has one logging path rather than two, and so it
            // satisfies CA1848 like every other call site in the app.
            LogSpeechMemoryFootprint(
                Log,
                phase,
                process.WorkingSet64 / 1024 / 1024,
                GC.GetTotalMemory(forceFullCollection: false) / 1024,
                null);
        }
        catch
        {
            // Diagnostics must never be able to break the feature they observe.
        }
    }

    // ---- Recording controls ----

    /// <summary>
    /// Clears the waveform panel to its empty state. Called when entering <c>Starting</c>, which
    /// is when the panel becomes <i>visible</i> 鈥?<c>RefreshSendButtonState</c> shows it for the
    /// whole of <c>IsVoiceInputActive</c>, not just while recording.
    ///
    /// <para>Doing this in <see cref="StartRecordingVisuals"/> instead was a bug: that runs on the
    /// transition to <c>Recording</c>, so for the half-second-plus that starting the recogniser
    /// takes, the panel was on screen still showing the <i>previous</i> session's elapsed time.</para>
    /// </summary>
    private void ResetRecordingVisuals()
    {
        WaveformTimerText.Text = FormatRecordingTime(TimeSpan.Zero);
        _smoothedAmplitude = 0;
        _sampleWritePos = 0;
        _sampleCount = 0;
        Array.Clear(_sampleRing);
        foreach (var bar in _waveformBars)
        {
            // Zero the scale as well as hiding: the bar keeps its transform across sessions, so a
            // restart would otherwise flash the previous recording's shape for one frame before
            // the first tick overwrote it.
            if (bar.RenderTransform is ScaleTransform scale) scale.ScaleY = 0;
            bar.Visibility = Visibility.Collapsed;
        }
    }

    private void StartRecordingVisuals()
    {
        // The clock starts when recording actually starts, not when the panel appeared: the
        // spinner time spent waiting for the recogniser is not part of the recording.
        _recordingStartedAt = DateTimeOffset.Now;
        ResetRecordingVisuals();
        InputBox.PlaceholderText = LocalizedStrings.Get(
            "VoiceListeningHint", "Listening... Enter to finish, Esc to discard");
        _recordingElapsedTimer.Start();
        _waveformTimer.Start();
    }

    private void StopRecordingVisuals()
    {
        _recordingElapsedTimer.Stop();
        _waveformTimer.Stop();
        _sampleCount = 0;
        _sampleWritePos = 0;
        Array.Clear(_sampleRing);
        InputBox.PlaceholderText = PlaceholderText;
    }

    private async void RecordingElapsedTimer_Tick(object? sender, object e)
    {
        var elapsed = DateTimeOffset.Now - _recordingStartedAt;
        if (VoiceCapturePolicy.ShouldFinish(elapsed))
        {
            WaveformTimerText.Text = FormatRecordingTime(MaxVoiceDuration);
            await FinishVoiceInputAsync();
            return;
        }

        WaveformTimerText.Text = FormatRecordingTime(elapsed);
    }

    private static string FormatRecordingTime(TimeSpan elapsed)
    {
        var current = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        var limit = $"{(int)MaxVoiceDuration.TotalMinutes}:{MaxVoiceDuration.Seconds:00}";
        return LocalizedStrings.Get("VoiceRecordingTimeFormat", "{0} / {1}")
            .Replace("{0}", current, StringComparison.Ordinal)
            .Replace("{1}", limit, StringComparison.Ordinal);
    }

    // ---- Lightweight XAML waveform ----

    /// <summary>Per-frame update: read amplitude, push to ring buffer.</summary>
    private void AdvanceWaveform()
    {
        var target = _voiceCapture.CurrentAmplitude;
        // Exponential moving average. Raw RMS jitters frame to frame and would render as noise;
        // this lags the signal slightly in exchange for bars that read as speech.
        _smoothedAmplitude = _smoothedAmplitude * (1 - SmoothingFactor) + target * SmoothingFactor;

        _sampleRing[_sampleWritePos] = Math.Clamp(_smoothedAmplitude, 0, 1);
        _sampleWritePos = (_sampleWritePos + 1) % MaxRingSamples;
        if (_sampleCount < MaxRingSamples) _sampleCount++;
    }

    private void WaveformTimer_Tick(object? sender, object e)
    {
        if (_recordingState != RecordingState.Recording) return;
        AdvanceWaveform();
        RenderWaveform();
    }

    private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var centerY = e.NewSize.Height / 2;
        WaveformBaseline.X1 = 0;
        WaveformBaseline.X2 = e.NewSize.Width;
        WaveformBaseline.Y1 = centerY;
        WaveformBaseline.Y2 = centerY;
        EnsureWaveformBars((int)(e.NewSize.Width / SampleStride));
        LayoutWaveformBars();
        RenderWaveform();
    }

    private void EnsureWaveformBars(int count)
    {
        count = Math.Clamp(count, 0, MaxRingSamples);
        while (_waveformBars.Count < count)
        {
            var bar = new Rectangle
            {
                Width = BarWidth,
                RadiusX = 1,
                RadiusY = 1,
                Fill = _waveformBrush,
                Visibility = Visibility.Collapsed,
                // Scaled about its own middle, so shrinking a full-height bar keeps it centred on
                // the baseline without also having to move it.
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 0 },
            };
            _waveformBars.Add(bar);
            WaveformCanvas.Children.Add(bar);
        }
    }

    /// <summary>
    /// Sizes and positions every bar at full scale. Split out of <see cref="RenderWaveform"/> and
    /// called only when the canvas resizes, because these are the properties that cost a layout
    /// pass 鈥?see the note there.
    /// </summary>
    private void LayoutWaveformBars()
    {
        var width = WaveformCanvas.ActualWidth;
        var centerY = WaveformCanvas.ActualHeight / 2;
        if (width <= 0 || centerY <= 2) return;

        var fullHalfHeight = centerY - 2;
        for (var index = 0; index < _waveformBars.Count; index++)
        {
            var bar = _waveformBars[index];
            bar.Height = fullHalfHeight * 2;
            Canvas.SetLeft(bar, width - BarWidth - index * SampleStride);
            Canvas.SetTop(bar, centerY - fullHalfHeight);
        }
    }

    /// <summary>
    /// Per-frame update, running at 30 fps for the whole recording.
    ///
    /// <para>It writes <b>only</b> <see cref="ScaleTransform.ScaleY"/>. A render transform does not
    /// participate in layout, so a frame costs a composition update and nothing else. The previous
    /// version set <c>bar.Height</c> 鈥?a layout property 鈥?on every bar every frame, which
    /// invalidated measure on the whole canvas 30 times a second (~130 bars on a typical composer
    /// width). <c>Canvas.Left</c>/<c>Top</c> moved out to <see cref="LayoutWaveformBars"/> for the
    /// same reason; they only change when the canvas resizes, so re-setting them per frame was
    /// pure waste even before the layout cost.</para>
    /// </summary>
    private void RenderWaveform()
    {
        var width = WaveformCanvas.ActualWidth;
        var centerY = WaveformCanvas.ActualHeight / 2;
        if (width <= 0 || centerY <= 2) return;

        // The floor keeps a silent bar as a visible 2px tick rather than nothing at all, matching
        // what Math.Max(1, 鈥? did when this was expressed as a height.
        var fullHalfHeight = centerY - 2;
        var minimumScale = 1 / fullHalfHeight;

        var visibleBars = Math.Min(_waveformBars.Count, (int)(width / SampleStride));
        var count = Math.Min(_sampleCount, visibleBars);
        for (var index = 0; index < visibleBars; index++)
        {
            var bar = _waveformBars[index];
            if (bar.RenderTransform is not ScaleTransform scale) continue;

            if (index >= count)
            {
                // Zero scale rather than Visibility.Collapsed: collapsing is a layout change, and
                // this branch is crossed on every frame until the ring buffer first fills.
                scale.ScaleY = 0;
                continue;
            }

            var ringIndex = ((_sampleWritePos - 1 - index) % MaxRingSamples + MaxRingSamples) % MaxRingSamples;
            scale.ScaleY = Math.Max(minimumScale, _sampleRing[ringIndex]);
            bar.Visibility = Visibility.Visible;
        }
    }

    // ---- Theme ----

    private void ApplyWaveformTheme()
    {
        var isDark = ActualTheme == ElementTheme.Dark
                  || (ActualTheme == ElementTheme.Default
                      && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        _waveformBrush.Color = ThemeService.GetColor("TextPrimaryColor", isDark);
    }

    // ---- Audio capture boundary ----

    private async Task StartAudioMeterAsync(CancellationToken cancellationToken)
    {
        await _voiceCapture.StartAsync(_preferredMicrophoneDeviceId, cancellationToken);
        _audioCaptureFailure = _voiceCapture.StartFailure;
    }

    private void StopAudioMeter(bool discardAudio = false)
        => _voiceCapture.Stop(discardAudio);
}
