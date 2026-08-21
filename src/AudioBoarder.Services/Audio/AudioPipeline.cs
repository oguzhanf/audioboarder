using System.Threading.Channels;
using AudioBoarder.Core.Audio;
using AudioBoarder.Core.Transcript;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioBoarder.Services.Audio;

/// <summary>
/// Wires one or more capture sources to a transcription service and writes
/// finalised segments to the supplied <see cref="TranscriptBuffer"/>.
///
/// Capture events are unbounded; we drop the oldest pending chunks if the
/// transcription consumer falls behind. Transcription is SERIALIZED through
/// a single consumer task so non-thread-safe transcribers (Whisper.net) are
/// safe and so chunks are processed in capture order.
/// </summary>
public sealed class AudioPipeline : IAsyncDisposable
{
    private readonly IReadOnlyList<IAudioCaptureSource> _sources;
    private readonly Func<ITranscriptionService> _transcriptionFactory;
    private readonly IVoiceActivityDetector _vad;
    private readonly TranscriptBuffer _buffer;
    private readonly ILogger<AudioPipeline> _logger;
    private CancellationTokenSource? _cts;
    private Channel<AudioChunk>? _channel;
    private Task? _consumer;
    private Task? _flusher;
    private ITranscriptionService? _activeTranscription;
    private bool _started;
    private readonly TimeSpan _flushInterval = TimeSpan.FromMilliseconds(250);
    private long _chunksReceived;
    private long _chunksTranscribed;
    private long _segmentsEmitted;
    private DateTimeOffset _firstChunkAt;
    private IStreamingTranscriptionService? _streamingService;

    public event EventHandler<TranscriptSegment>? SegmentEmitted;
    public event EventHandler<TranscriptSegment>? InterimEmitted;
    public event EventHandler<AudioCaptureError>? CaptureFailed;

    public AudioPipeline(
        IEnumerable<IAudioCaptureSource> sources,
        Func<ITranscriptionService> transcriptionFactory,
        IVoiceActivityDetector vad,
        TranscriptBuffer buffer,
        ILogger<AudioPipeline>? logger = null)
    {
        _sources = sources?.ToList() ?? throw new ArgumentNullException(nameof(sources));
        _transcriptionFactory = transcriptionFactory ?? throw new ArgumentNullException(nameof(transcriptionFactory));
        _vad = vad ?? throw new ArgumentNullException(nameof(vad));
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _logger = logger ?? NullLogger<AudioPipeline>.Instance;
    }

    /// <summary>Backwards-compatible overload that captures the service eagerly.</summary>
    public AudioPipeline(
        IEnumerable<IAudioCaptureSource> sources,
        ITranscriptionService transcription,
        IVoiceActivityDetector vad,
        TranscriptBuffer buffer,
        ILogger<AudioPipeline>? logger = null)
        : this(sources, () => transcription, vad, buffer, logger)
    {
    }

    public bool IsRunning => _started;
    public long ChunksReceived => Interlocked.Read(ref _chunksReceived);
    public long ChunksTranscribed => Interlocked.Read(ref _chunksTranscribed);
    public long SegmentsEmitted => Interlocked.Read(ref _segmentsEmitted);

    /// <summary>
    /// Leaky-peak amplitude (0..1) of recently captured audio. Stays ~0 when the
    /// mic is muted/silent, letting the UI tell a "no signal" mic apart from one
    /// that simply hasn't produced a transcript yet.
    /// </summary>
    public double RecentPeakAmplitude { get { lock (_peakGate) return _recentPeak; } }
    private double _recentPeak;
    private readonly object _peakGate = new();

    public async Task StartAsync(CancellationToken ct)
    {
        if (_started) return;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Interlocked.Exchange(ref _chunksReceived, 0);
        Interlocked.Exchange(ref _chunksTranscribed, 0);
        Interlocked.Exchange(ref _segmentsEmitted, 0);
        _firstChunkAt = default;
        lock (_peakGate) _recentPeak = 0;

        _channel = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(capacity: 256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        var transcription = _transcriptionFactory();
        _activeTranscription = transcription;
        if (!transcription.IsReady)
            await transcription.InitializeAsync(_cts.Token).ConfigureAwait(false);
        _logger.LogInformation("Audio pipeline starting; transcription={Name} vad={Vad}",
            transcription.Name, _vad.GetType().Name);

        // If the transcription service streams segments asynchronously (Azure Speech),
        // subscribe to its event so we can forward them as captions.
        if (transcription is IStreamingTranscriptionService streaming)
        {
            streaming.SegmentReady += OnStreamingSegmentReady;
            streaming.InterimReady += OnStreamingInterim;
            _streamingService = streaming;
        }

        _consumer = Task.Run(() => ConsumeAsync(transcription, _channel.Reader, _cts.Token), _cts.Token);
        _flusher = Task.Run(() => FlushLoopAsync(transcription, _cts.Token), _cts.Token);

        foreach (var src in _sources)
        {
            src.CaptureFailed += OnSourceFailed;
            src.ChunkCaptured += OnChunkCaptured;
            await src.StartAsync(_cts.Token).ConfigureAwait(false);
        }
        _started = true;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (!_started) return;
        foreach (var src in _sources)
        {
            try { await src.StopAsync(ct).ConfigureAwait(false); } catch { /* swallow */ }
            src.ChunkCaptured -= OnChunkCaptured;
            src.CaptureFailed -= OnSourceFailed;
        }
        if (_streamingService is not null)
        {
            _streamingService.SegmentReady -= OnStreamingSegmentReady;
            _streamingService.InterimReady -= OnStreamingInterim;
            _streamingService = null;
        }
        _channel?.Writer.TryComplete();

        // Final force-flush BEFORE cancelling so the last spoken utterance (which
        // is still sitting in the transcriber's buffer waiting for the silence
        // window) is transcribed and emitted instead of being discarded.
        if (_activeTranscription is not null)
        {
            try
            {
                var finalSegs = await _activeTranscription.FlushAsync(CancellationToken.None, force: true)
                    .ConfigureAwait(false);
                EmitSegments(finalSegs);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Final flush on stop failed"); }
        }

        _cts?.Cancel();
        try
        {
            if (_consumer is not null) await _consumer.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            if (_flusher is not null) await _flusher.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch { /* swallow */ }
        _activeTranscription = null;
        _started = false;
    }

    private void OnStreamingSegmentReady(object? sender, TranscriptSegment segment)
    {
        Interlocked.Increment(ref _chunksTranscribed);
        _buffer.Append(segment);
        Interlocked.Increment(ref _segmentsEmitted);
        SegmentEmitted?.Invoke(this, segment);
    }

    // Interim/partial hypotheses are NOT committed to the transcript buffer —
    // they are provisional and superseded by the final SegmentReady. They only
    // drive the live "typing as you speak" display.
    private void OnStreamingInterim(object? sender, TranscriptSegment interim)
        => InterimEmitted?.Invoke(this, interim);

    private void OnSourceFailed(object? sender, AudioCaptureError err)
        => CaptureFailed?.Invoke(this, err);

    private void OnChunkCaptured(object? sender, AudioChunk chunk)
    {
        if (_channel is null) return;
        if (Interlocked.Increment(ref _chunksReceived) == 1)
        {
            _firstChunkAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("First audio chunk received; role={Role} bytes={Bytes}",
                chunk.Role, chunk.Samples.Length);
        }
        UpdatePeak(chunk);
        // Drop-oldest channel: TryWrite never blocks even when full.
        _channel.Writer.TryWrite(chunk);
    }

    private void UpdatePeak(AudioChunk chunk)
    {
        var span = chunk.Samples.Span;
        if (chunk.Format.BitsPerSample != 16 || span.Length < 2) return;
        short max = 0;
        for (var i = 0; i + 1 < span.Length; i += 2)
        {
            var s = (short)(span[i] | (span[i + 1] << 8));
            var a = (short)(s == short.MinValue ? short.MaxValue : Math.Abs(s));
            if (a > max) max = a;
        }
        var peak = max / 32768.0;
        lock (_peakGate) _recentPeak = Math.Max(peak, _recentPeak * 0.85);
    }

    private async Task ConsumeAsync(ITranscriptionService transcription, ChannelReader<AudioChunk> reader, CancellationToken ct)
    {
        // Streaming backends (Azure Speech) run their own server-side VAD and need
        // a continuous, gapless audio feed, so we must NOT drop chunks for them.
        //
        // Windowed backends used to have every sub-threshold 30 ms chunk removed
        // before buffering, which spliced the surviving fragments together. That
        // destroys transcription: inter-word pauses, quiet fricatives and word
        // onsets all fall under a fixed RMS threshold. Measured on a 7.5 s sample,
        // a quiet microphone lost 63% of its audio and the model returned
        // "we're going [unclear]. We're going the model and model." for
        // "we're going to talk about Azure AI Foundry ... models and model
        // deployments", which the same audio transcribed perfectly when sent
        // continuously.
        //
        // So the VAD no longer decides WHICH audio is kept — only WHEN an
        // utterance starts and ends. Inside an utterance every chunk is buffered,
        // gaps included, exactly as a meeting client would send it.
        var isStreaming = transcription is IStreamingTranscriptionService;
        long received = 0, buffered = 0, speechChunks = 0;
        var lastStat = DateTimeOffset.UtcNow;

        // Speech detected this recently keeps the utterance open, so natural pauses
        // stay in the audio instead of being cut out of the middle of a sentence.
        var holdover = TimeSpan.FromMilliseconds(700);
        var lastSpeechAt = DateTimeOffset.MinValue;

        // Small pre-roll so the attack of the first word isn't clipped: the VAD only
        // trips once a sound is already underway.
        const int preRollChunks = 6; // ~180 ms at 30 ms/chunk
        var preRoll = new Queue<AudioChunk>(preRollChunks + 1);
        var inUtterance = false;

        try
        {
            await foreach (var chunk in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                received++;
                var now = DateTimeOffset.UtcNow;
                var isSpeech = isStreaming || _vad.IsSpeech(chunk);
                if (isSpeech) { lastSpeechAt = now; speechChunks++; }

                if ((now - lastStat).TotalSeconds >= 2)
                {
                    double peak; lock (_peakGate) peak = _recentPeak;
                    _logger.LogInformation(
                        "Pipeline stats: received={Received} buffered={Buffered} speech={Speech} recentPeak={Peak:F3} vad={Vad}",
                        received, buffered, speechChunks, peak, _vad.GetType().Name);
                    lastStat = now;
                }

                if (isStreaming)
                {
                    await ForwardAsync(transcription, chunk, ct).ConfigureAwait(false);
                    buffered++;
                    continue;
                }

                var utteranceOpen = lastSpeechAt != DateTimeOffset.MinValue && (now - lastSpeechAt) <= holdover;
                if (!utteranceOpen)
                {
                    // Silence between utterances: hold the newest chunks as pre-roll
                    // rather than discarding them outright.
                    inUtterance = false;
                    preRoll.Enqueue(chunk);
                    while (preRoll.Count > preRollChunks) preRoll.Dequeue();
                    continue;
                }

                if (!inUtterance)
                {
                    inUtterance = true;
                    while (preRoll.Count > 0)
                    {
                        await ForwardAsync(transcription, preRoll.Dequeue(), ct).ConfigureAwait(false);
                        buffered++;
                    }
                }

                await ForwardAsync(transcription, chunk, ct).ConfigureAwait(false);
                buffered++;
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transcription consumer crashed");
        }
    }

    /// <summary>
    /// Hands one chunk to the transcription service. Windowed services buffer it and
    /// return nothing; streaming services may emit segments immediately.
    /// </summary>
    private async Task ForwardAsync(ITranscriptionService transcription, AudioChunk chunk, CancellationToken ct)
    {
        try
        {
            var segments = await transcription.TranscribeAsync(chunk, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _chunksTranscribed);
            EmitSegments(segments);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Transcribe failed for chunk; continuing");
        }
    }

    private async Task FlushLoopAsync(ITranscriptionService transcription, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Poll frequently: FlushAsync only emits when the speaker has paused,
                // so a short interval gives end-of-utterance latency near the pause
                // length itself rather than a fixed multi-second window.
                try { await Task.Delay(_flushInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                try
                {
                    var segments = await transcription.FlushAsync(ct).ConfigureAwait(false);
                    EmitSegments(segments);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Flush failed");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Flush loop crashed");
        }
    }

    private void EmitSegments(IReadOnlyList<TranscriptSegment> segments)
    {
        foreach (var s in segments)
        {
            _buffer.Append(s);
            Interlocked.Increment(ref _segmentsEmitted);
            SegmentEmitted?.Invoke(this, s);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var src in _sources) await src.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
