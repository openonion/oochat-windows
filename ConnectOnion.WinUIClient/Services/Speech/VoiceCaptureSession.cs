using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ConnectOnion.WinUIClient.Services.Speech;
using Microsoft.Extensions.Logging;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using WinRT;

namespace ConnectOnion.WinUIClient.Services;

internal enum VoiceCaptureStartFailure
{
    None,
    AccessDenied,
    NoDevice,
    Unavailable,
}

/// <summary>
/// Owns one microphone capture lifetime. WinRT device selection, AudioGraph teardown and PCM
/// buffering stay here so the composer only coordinates UI state and transcription.
/// </summary>
internal sealed class VoiceCaptureSession : IDisposable
{
    private const ushort ChannelCount = 1;
    private const ushort BitsPerSample = 16;

    private static readonly Action<ILogger, string, Exception?> LogPreferredMicrophoneUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "PreferredMicrophoneUnavailable"),
            "The microphone chosen in Settings ({DeviceId}) could not be opened; falling back to the system default");

    private static readonly Action<ILogger, Exception?> LogDefaultMicrophoneUnavailable =
        LoggerMessage.Define(LogLevel.Warning, new EventId(2, "DefaultMicrophoneUnavailable"),
            "The default capture device could not be resolved");

    private static readonly Action<ILogger, string, Exception?> LogCaptureUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, "AudioCaptureUnavailable"),
            "The microphone capture graph could not start ({Reason})");

    private static ILogger Log => AppServices.Logging.CreateLogger<VoiceCaptureSession>();

    private readonly object _audioLock = new();
    private readonly object _amplitudeLock = new();
    private MemoryStream? _capturedPcm;
    private long _capturedSampleCount;
    private long _voicedSampleCount;
    private double _amplitude;
    private AudioGraph? _audioGraph;
    private AudioDeviceInputNode? _audioInputNode;
    private AudioFrameOutputNode? _audioFrameOutputNode;
    private int _disposed;

    public VoiceCaptureStartFailure StartFailure { get; private set; }

    public double CurrentAmplitude
    {
        get
        {
            lock (_amplitudeLock) return _amplitude;
        }
    }

    public async Task StartAsync(string? preferredDeviceId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Stop(discardAudio: true);
        ResetCapturedAudio();
        StartFailure = VoiceCaptureStartFailure.None;

        try
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.ClosestToDesired,
                DesiredSamplesPerQuantum = 480,
            };
            var graphResult = await AudioGraph.CreateAsync(settings);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
            {
                StartFailure = graphResult.Status == AudioGraphCreationStatus.DeviceNotAvailable
                    ? VoiceCaptureStartFailure.NoDevice
                    : VoiceCaptureStartFailure.Unavailable;
                LogCaptureUnavailable(Log, graphResult.Status.ToString(), null);
                return;
            }

            _audioGraph = graphResult.Graph;
            cancellationToken.ThrowIfCancellationRequested();
            var captureDevice = await ResolveCaptureDeviceAsync(preferredDeviceId, cancellationToken);
            var inputResult = captureDevice is null
                ? await _audioGraph.CreateDeviceInputNodeAsync(MediaCategory.Other)
                : await _audioGraph.CreateDeviceInputNodeAsync(
                    MediaCategory.Other, _audioGraph.EncodingProperties, captureDevice);
            if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                StartFailure = inputResult.Status switch
                {
                    AudioDeviceNodeCreationStatus.AccessDenied => VoiceCaptureStartFailure.AccessDenied,
                    AudioDeviceNodeCreationStatus.DeviceNotAvailable => VoiceCaptureStartFailure.NoDevice,
                    _ => VoiceCaptureStartFailure.Unavailable,
                };
                LogCaptureUnavailable(Log, inputResult.Status.ToString(), null);
                Stop(discardAudio: true);
                return;
            }

            _audioInputNode = inputResult.DeviceInputNode;
            cancellationToken.ThrowIfCancellationRequested();
            var frameEncoding = _audioGraph.EncodingProperties.Copy();
            frameEncoding.Subtype = "Float";
            frameEncoding.SampleRate = VoiceCapturePolicy.SampleRate;
            frameEncoding.ChannelCount = ChannelCount;
            frameEncoding.BitsPerSample = sizeof(float) * 8;
            frameEncoding.Bitrate = VoiceCapturePolicy.SampleRate * ChannelCount * sizeof(float) * 8;
            _audioFrameOutputNode = _audioGraph.CreateFrameOutputNode(frameEncoding);
            _audioInputNode.AddOutgoingConnection(_audioFrameOutputNode);
            _audioGraph.QuantumStarted += AudioGraph_QuantumStarted;
            _audioFrameOutputNode.Start();
            _audioInputNode.Start();
            _audioGraph.Start();
        }
        catch (OperationCanceledException)
        {
            Stop(discardAudio: true);
            throw;
        }
        catch (Exception ex)
        {
            StartFailure = ex is UnauthorizedAccessException
                ? VoiceCaptureStartFailure.AccessDenied
                : VoiceCaptureStartFailure.Unavailable;
            LogCaptureUnavailable(Log, ex.GetType().Name, ex);
            Stop(discardAudio: true);
        }
    }

    public void Stop(bool discardAudio = false)
    {
        if (_audioGraph is not null)
            _audioGraph.QuantumStarted -= AudioGraph_QuantumStarted;
        try { _audioGraph?.Stop(); } catch { }

        _audioFrameOutputNode?.Dispose();
        _audioInputNode?.Dispose();
        _audioGraph?.Dispose();
        _audioFrameOutputNode = null;
        _audioInputNode = null;
        _audioGraph = null;
        lock (_amplitudeLock) _amplitude = 0;
        if (discardAudio) DiscardCapturedAudio();
    }

    public bool HasCapturedSpeech()
    {
        lock (_audioLock)
            return VoiceActivityDetector.HasSufficientSpeech(
                _voicedSampleCount, VoiceCapturePolicy.SampleRate);
    }

    public byte[] TakeWaveAudio()
    {
        byte[] pcm;
        lock (_audioLock)
        {
            pcm = _capturedPcm?.ToArray() ?? [];
            _capturedPcm?.Dispose();
            _capturedPcm = null;
            _capturedSampleCount = 0;
            _voicedSampleCount = 0;
        }

        return PcmWaveEncoder.Encode(
            pcm, VoiceCapturePolicy.SampleRate, checked((short)ChannelCount), checked((short)BitsPerSample));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop(discardAudio: true);
    }

    private static async Task<DeviceInformation?> ResolveCaptureDeviceAsync(
        string? preferredDeviceId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(preferredDeviceId))
        {
            try
            {
                var preferred = await DeviceInformation.CreateFromIdAsync(preferredDeviceId);
                cancellationToken.ThrowIfCancellationRequested();
                if (preferred is not null) return preferred;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogPreferredMicrophoneUnavailable(Log, preferredDeviceId, ex);
            }
        }

        try
        {
            var id = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
            if (!string.IsNullOrEmpty(id))
                return await DeviceInformation.CreateFromIdAsync(id);
        }
        catch (Exception ex)
        {
            LogDefaultMicrophoneUnavailable(Log, ex);
        }

        return null;
    }

    private void AudioGraph_QuantumStarted(AudioGraph sender, object args)
    {
        if (_audioFrameOutputNode is null) return;
        using var frame = _audioFrameOutputNode.GetFrame();
        var rms = CapturePcmAndReadAmplitude(frame);
        lock (_amplitudeLock) _amplitude = VoiceWaveformLevel.FromRms(rms);
    }

    private unsafe double CapturePcmAndReadAmplitude(AudioFrame frame)
    {
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        var byteAccess = reference.As<IMemoryBufferByteAccess>();
        byteAccess.GetBuffer(out var dataInBytes, out var capacity);
        var validLength = Math.Min(buffer.Length, capacity);
        var floatByteCount = checked((int)(validLength - validLength % sizeof(float)));
        var sampleCount = floatByteCount / sizeof(float);
        if (sampleCount <= 0) return 0;

        var pcmByteCount = FloatPcm16Converter.GetRequiredByteCount(sampleCount);
        var rentedPcm = ArrayPool<byte>.Shared.Rent(pcmByteCount);
        try
        {
            var samples = new ReadOnlySpan<float>((float*)dataInBytes, sampleCount);
            var pcm = rentedPcm.AsSpan(0, pcmByteCount);
            var rms = FloatPcm16Converter.ConvertAndMeasureAcRms(samples, pcm);
            lock (_audioLock)
            {
                var remainingSamples = Math.Max(0, VoiceCapturePolicy.MaxSamples - _capturedSampleCount);
                var samplesToKeep = checked((int)Math.Min(sampleCount, remainingSamples));
                if (_capturedPcm is not null && samplesToKeep > 0)
                {
                    _capturedPcm.Write(pcm[..FloatPcm16Converter.GetRequiredByteCount(samplesToKeep)]);
                    _capturedSampleCount += samplesToKeep;
                    if (VoiceActivityDetector.IsVoicedFrame(rms))
                        _voicedSampleCount += samplesToKeep;
                }
            }
            return rms;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedPcm);
        }
    }

    private void ResetCapturedAudio()
    {
        lock (_audioLock)
        {
            _capturedPcm?.Dispose();
            _capturedPcm = new MemoryStream();
            _capturedSampleCount = 0;
            _voicedSampleCount = 0;
        }
    }

    private void DiscardCapturedAudio()
    {
        lock (_audioLock)
        {
            _capturedPcm?.Dispose();
            _capturedPcm = null;
            _capturedSampleCount = 0;
            _voicedSampleCount = 0;
        }
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
