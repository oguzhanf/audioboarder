using AudioBoarder.Core.Audio;
using AudioBoarder.Services.Transcription;

namespace AudioBoarder.Tests.Transcription;

public class WhisperTranscriptionServiceTests
{
    [Fact]
    public async Task Initialize_UsesInjectedLoader_WhenSet()
    {
        var loaderCalled = false;
        var svc = new WhisperTranscriptionService(new WhisperOptions("tiny", AutoDownload: false))
        {
            ModelLoader = (_, _) => { loaderCalled = true; return Task.CompletedTask; },
        };
        await svc.InitializeAsync(CancellationToken.None);
        loaderCalled.Should().BeTrue();
        svc.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task Transcribe_UsesInjectedTranscriber_WhenSet()
    {
        var svc = new WhisperTranscriptionService(new WhisperOptions("tiny", AutoDownload: false))
        {
            ModelLoader = (_, _) => Task.CompletedTask,
            Transcriber = (_, _) => Task.FromResult<IReadOnlyList<Core.Transcript.TranscriptSegment>>(new[]
            {
                new Core.Transcript.TranscriptSegment(Guid.NewGuid(), Core.Transcript.TranscriptSpeaker.Local, "hi", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            }),
        };
        await svc.InitializeAsync(CancellationToken.None);
        var chunk = new AudioChunk
        {
            Role = AudioStreamRole.Microphone,
            Format = AudioFormat.Mono16kPcm16,
            CapturedAt = DateTimeOffset.UtcNow,
            Samples = new byte[3200],
        };
        var segs = await svc.TranscribeAsync(chunk, CancellationToken.None);
        segs.Should().HaveCount(1);
        segs[0].Text.Should().Be("hi");
    }
}
