using AudioBoarder.Core.Audio;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.Audio;

namespace AudioBoarder.Tests.Audio;

/// <summary>
/// Guards the audio continuity contract for windowed transcription backends.
///
/// The pipeline used to drop every 30 ms chunk that scored below the energy-VAD
/// threshold BEFORE buffering, splicing the survivors together. Measured on a
/// 7.5 s sample at a realistic quiet microphone level that removed 63% of the
/// audio, and gpt-transcribe returned "we're going model. We're going the model
/// and model." for speech it transcribed perfectly when sent continuously.
/// </summary>
public class AudioPipelineContinuityTests
{
    private static readonly AudioFormat Fmt = AudioFormat.Mono16kPcm16;

    /// <summary>30 ms of PCM at the given amplitude (0..1).</summary>
    private static AudioChunk Chunk(double amplitude)
    {
        var samples = (int)(Fmt.SampleRate * 0.03);
        var bytes = new byte[samples * 2];
        var value = (short)(amplitude * short.MaxValue);
        for (var i = 0; i < samples; i++)
        {
            // Alternate sign so RMS reflects amplitude rather than DC offset.
            var s = (i % 2 == 0) ? value : (short)-value;
            bytes[i * 2] = (byte)(s & 0xFF);
            bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return new AudioChunk
        {
            Role = AudioStreamRole.Microphone,
            Format = Fmt,
            CapturedAt = DateTimeOffset.UtcNow,
            Samples = bytes,
        };
    }

    private sealed class FakeSource : IAudioCaptureSource
    {
        public AudioStreamRole Role => AudioStreamRole.Microphone;
        public AudioFormat OutputFormat => Fmt;
        public bool IsRunning { get; private set; }
        public event EventHandler<AudioChunk>? ChunkCaptured;
        public event EventHandler<AudioCaptureError>? CaptureFailed;
        public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Emit(AudioChunk c) => ChunkCaptured?.Invoke(this, c);
        public void FailWith(AudioCaptureError e) => CaptureFailed?.Invoke(this, e);
    }

    /// <summary>Records every chunk handed to it, like the windowed cloud services do.</summary>
    private sealed class RecordingTranscription : ITranscriptionService
    {
        public readonly List<AudioChunk> Received = new();
        public string Name => "recording";
        public bool IsReady => true;
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(AudioChunk chunk, CancellationToken ct)
        {
            lock (Received) Received.Add(chunk);
            return Task.FromResult<IReadOnlyList<TranscriptSegment>>(Array.Empty<TranscriptSegment>());
        }
        public Task<IReadOnlyList<TranscriptSegment>> FlushAsync(CancellationToken ct, bool force = false)
            => Task.FromResult<IReadOnlyList<TranscriptSegment>>(Array.Empty<TranscriptSegment>());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public int Count { get { lock (Received) return Received.Count; } }
    }

    private static async Task<(RecordingTranscription Sink, FakeSource Src, AudioPipeline Pipe)> StartAsync(double vadThreshold)
    {
        var sink = new RecordingTranscription();
        var src = new FakeSource();
        var pipe = new AudioPipeline(
            new[] { src },
            () => sink,
            new EnergyVoiceActivityDetector(vadThreshold),
            new TranscriptBuffer(TimeSpan.FromMinutes(5)));
        await pipe.StartAsync(CancellationToken.None);
        return (sink, src, pipe);
    }

    private static async Task DrainAsync(RecordingTranscription sink, int expectedAtLeast)
    {
        for (var i = 0; i < 60 && sink.Count < expectedAtLeast; i++)
            await Task.Delay(25);
        await Task.Delay(75);
    }

    [Fact]
    public async Task QuietAudioInsideAnUtteranceIsStillForwarded()
    {
        var (sink, src, pipe) = await StartAsync(0.05);
        await using var _ = pipe;

        // Loud, then a quiet stretch that the VAD scores as non-speech, then loud.
        // The quiet part is an inter-word gap and MUST survive: removing it is what
        // produced spliced, mistranscribed audio.
        src.Emit(Chunk(0.30));
        for (var i = 0; i < 5; i++) src.Emit(Chunk(0.001));
        src.Emit(Chunk(0.30));

        await DrainAsync(sink, 7);

        sink.Count.Should().BeGreaterThanOrEqualTo(7,
            "every chunk between two speech chunks must reach the transcriber");
    }

    [Fact]
    public async Task LeadingSilenceBeforeAnySpeechIsNotBuffered()
    {
        var (sink, src, pipe) = await StartAsync(0.05);
        await using var _ = pipe;

        // Pure silence with no speech at all must not be sent: that would waste
        // API calls and invite hallucinated text on empty audio.
        for (var i = 0; i < 12; i++) src.Emit(Chunk(0.0005));
        await Task.Delay(300);

        sink.Count.Should().Be(0);
    }

    [Fact]
    public async Task SpeechOnsetIsPrecededByPreRollSoWordsAreNotClipped()
    {
        var (sink, src, pipe) = await StartAsync(0.05);
        await using var _ = pipe;

        // Quiet lead-in (the attack of a word sits below the threshold), then speech.
        for (var i = 0; i < 6; i++) src.Emit(Chunk(0.002));
        src.Emit(Chunk(0.30));

        await DrainAsync(sink, 2);

        sink.Count.Should().BeGreaterThan(1,
            "pre-roll must replay the chunks just before the VAD tripped");
    }
}
