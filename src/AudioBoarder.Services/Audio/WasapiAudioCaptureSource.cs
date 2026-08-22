using AudioBoarder.Core.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioBoarder.Services.Audio;

/// <summary>
/// Real WASAPI capture via NAudio. Resamples device input to 16 kHz mono
/// PCM-16 — the canonical format the rest of the pipeline expects.
/// </summary>
/// <remarks>
/// Windows-only. Construct one instance per logical role (mic / loopback).
/// </remarks>
public sealed class WasapiAudioCaptureSource : IAudioCaptureSource
{
    private readonly ILogger<WasapiAudioCaptureSource> _logger;
    private readonly AudioStreamRole _role;
    private readonly bool _loopback;
    private readonly AudioDeviceService? _deviceService;
    private readonly bool _autoGain;
    private readonly float _agcMaxGain;
    private float _agcGain = 1f;
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private MediaFoundationResampler? _resampler;
    private BufferedWaveProvider? _buffer;
    private byte[] _scratch = Array.Empty<byte>();

    public WasapiAudioCaptureSource(AudioStreamRole role, ILogger<WasapiAudioCaptureSource>? logger = null)
        : this(role, null, logger)
    {
    }

    public WasapiAudioCaptureSource(AudioStreamRole role, AudioDeviceService? deviceService,
        ILogger<WasapiAudioCaptureSource>? logger = null, bool autoGain = true, float agcMaxGain = 20f)
    {
        _role = role;
        _loopback = role == AudioStreamRole.Loopback;
        _deviceService = deviceService;
        _autoGain = autoGain;
        _agcMaxGain = agcMaxGain;
        _logger = logger ?? NullLogger<WasapiAudioCaptureSource>.Instance;
    }

    public AudioStreamRole Role => _role;
    public AudioFormat OutputFormat => AudioFormat.Mono16kPcm16;
    public bool IsRunning { get; private set; }

    public event EventHandler<AudioChunk>? ChunkCaptured;
    public event EventHandler<AudioCaptureError>? CaptureFailed;

    public Task StartAsync(CancellationToken ct)
    {
        if (IsRunning) return Task.CompletedTask;
        _agcGain = 1f;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = _loopback
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, NAudio.CoreAudioApi.Role.Console)
                : (_deviceService?.ResolveMicrophone()
                   ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, NAudio.CoreAudioApi.Role.Communications));

            _capture = _loopback
                ? new WasapiLoopbackCapture(_device)
                : new WasapiCapture(_device, useEventSync: true, audioBufferMillisecondsLength: 30);

            // Capture in SHARED mode (never exclusive) so other apps — notably
            // Teams during a meeting — keep using the same microphone at the same
            // time. Exclusive mode would lock the device and break the call.
            if (_capture is WasapiCapture wc)
                wc.ShareMode = NAudio.CoreAudioApi.AudioClientShareMode.Shared;

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            _buffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                // Return only actually-buffered audio. Default (true) zero-pads
                // every Read up to the requested size, which floods the resampler
                // (and the whole pipeline) with silence and drowns real speech.
                ReadFully = false,
            };
            var targetFormat = new WaveFormat(OutputFormat.SampleRate, OutputFormat.BitsPerSample, OutputFormat.Channels);
            _resampler = new MediaFoundationResampler(_buffer, targetFormat) { ResamplerQuality = 60 };

            _capture.StartRecording();
            IsRunning = true;

            // Surface a Windows-level endpoint mute immediately. Capture "succeeds"
            // on a muted endpoint and then delivers pure silence forever with no
            // error, which is indistinguishable from nobody speaking. Teams and
            // headset vendor software sync their mute button to this flag.
            if (!_loopback)
            {
                try
                {
                    if (_device.AudioEndpointVolume.Mute)
                        _logger.LogWarning(
                            "Capture endpoint \"{Device}\" is MUTED in Windows — all captured audio will be silent " +
                            "until it is unmuted (Teams and headset mute buttons set this flag)",
                            _device.FriendlyName);
                }
                catch { /* endpoint may not expose volume */ }
            }
            _logger.LogInformation("WASAPI capture started role={Role} device={Device} format={Format}",
                _role, _device?.FriendlyName, _capture!.WaveFormat);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WASAPI capture start failed for role {Role}", _role);
            CaptureFailed?.Invoke(this, new AudioCaptureError(_role, ex.Message, ex));
            DisposeNative();
            throw;
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (!IsRunning) return Task.CompletedTask;
        try { _capture?.StopRecording(); } catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stop capture failed for role {Role}", _role);
        }
        // Release the native COM objects (MF resampler, WASAPI client, device
        // endpoint) and detach handlers. The source is a singleton reused across
        // Listen-toggles and mic switches; without this, every Stop→Start cycle
        // leaked one of each and capture eventually failed to start (no captions).
        DisposeNative();
        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _buffer is null || _resampler is null) return;
        try
        {
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

            // One Read per callback into a fixed buffer with ~1s of output
            // headroom. A single Read avoids the MF_E_NOTACCEPTING (0xC00D36B5)
            // that back-to-back Reads trigger; anything not drained this callback
            // stays in the 2s BufferedWaveProvider and is read on the next one.
            if (_scratch.Length == 0)
                _scratch = new byte[Math.Max(8192, _resampler.WaveFormat.AverageBytesPerSecond)];
            var got = _resampler.Read(_scratch, 0, _scratch.Length);
            if (got <= 0) return;

            var copy = new byte[got];
            Buffer.BlockCopy(_scratch, 0, copy, 0, got);
            if (_autoGain) ApplyAutoGain(copy, got);
            ChunkCaptured?.Invoke(this, new AudioChunk
            {
                Role = _role,
                Format = OutputFormat,
                CapturedAt = DateTimeOffset.UtcNow,
                Samples = copy,
            });
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Transient Media Foundation hiccup — skip this callback, keep capturing.
            _logger.LogDebug(ex, "Resampler transient skip for role {Role}", _role);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resampling failed for role {Role}", _role);
            CaptureFailed?.Invoke(this, new AudioCaptureError(_role, ex.Message, ex));
        }
    }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            IsRunning = false;
            if (e.Exception is not null)
            {
                _logger.LogError(e.Exception, "WASAPI capture stopped with error role={Role}", _role);
                CaptureFailed?.Invoke(this, new AudioCaptureError(_role, e.Exception.Message, e.Exception));
            }
        }

        /// <summary>
        /// Smoothed automatic gain control. Boosts quiet microphones (e.g. a headset
        /// capturing at ~2% peak) toward a usable level so the VAD and transcriber
        /// actually see speech, while never amplifying a truly dead/silent mic into
        /// noise and never attenuating an already-loud one. Hard-clips to int16.
        /// </summary>
        private void ApplyAutoGain(byte[] pcm, int length)
        {
            const float target = 0.28f;     // desired peak (~ -11 dBFS)
            const float floor = 0.0008f;    // below this we treat the chunk as silence
            short maxAbs = 0;
            for (var i = 0; i + 1 < length; i += 2)
            {
                var s = (short)(pcm[i] | (pcm[i + 1] << 8));
                var a = s == short.MinValue ? short.MaxValue : Math.Abs((int)s);
                if (a > maxAbs) maxAbs = (short)a;
            }
            var peak = maxAbs / 32768f;

            // Hold gain through silence (avoids pumping); adapt on real signal only.
            var desired = peak < floor ? _agcGain : Math.Clamp(target / peak, 1f, _agcMaxGain);
            _agcGain = _agcGain * 0.9f + desired * 0.1f;
            if (_agcGain <= 1.02f) return; // negligible boost — leave samples untouched

            for (var i = 0; i + 1 < length; i += 2)
            {
                var s = (short)(pcm[i] | (pcm[i + 1] << 8));
                var v = (int)(s * _agcGain);
                if (v > short.MaxValue) v = short.MaxValue;
                else if (v < short.MinValue) v = short.MinValue;
                pcm[i] = (byte)(v & 0xFF);
                pcm[i + 1] = (byte)((v >> 8) & 0xFF);
            }
        }

    public ValueTask DisposeAsync()
    {
        DisposeNative();
        return ValueTask.CompletedTask;
    }

    private void DisposeNative()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.Dispose(); } catch { /* ignore */ }
            _capture = null;
        }
        _resampler?.Dispose(); _resampler = null;
        _buffer = null;
        _device?.Dispose(); _device = null;
        IsRunning = false;
    }
}
