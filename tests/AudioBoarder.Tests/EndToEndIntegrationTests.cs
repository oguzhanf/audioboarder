using AudioBoarder.Core.Audio;
using AudioBoarder.Core.Layout;
using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services;
using AudioBoarder.Services.Audio;
using AudioBoarder.Services.Layout;
using AudioBoarder.Tests.Fakes;

namespace AudioBoarder.Tests;

public class EndToEndIntegrationTests
{
    [Fact]
    public async Task ProductionOrchestrator_WithFakeGenerator_BuildsDiagram()
    {
        var scene = new SceneGraph();
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(5));

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 6; i++)
        {
            buffer.Append(new TranscriptSegment(Guid.NewGuid(),
                i % 2 == 0 ? TranscriptSpeaker.Local : TranscriptSpeaker.Remote,
                $"step {i}", now.AddSeconds(i), now.AddSeconds(i + 1)));
        }

        var orchestrator = new DiagramOrchestrator(
            new InMemoryScenePatchGenerator(),
            new LayeredLayoutEngine(),
            buffer,
            scene);

        var result = await orchestrator.GenerateAsync(userInstruction: "first pass");

        scene.Nodes.Count.Should().BeGreaterThan(0);
        scene.Notes.Count.Should().BeGreaterThan(0);
        result.LayoutResult.NodesPositioned.Should().Be(scene.Nodes.Count);
    }

    [Fact]
    public async Task AudioPipeline_DrivesTranscriptBuffer()
    {
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(1));
        var transcription = new ScriptedTranscriptionService(new[]
        {
            (TranscriptSpeaker.Local, "hello"),
            (TranscriptSpeaker.Remote, "hi back"),
        }, segmentSpacing: TimeSpan.FromMilliseconds(1));
        await transcription.InitializeAsync(CancellationToken.None);

        var samples = new byte[3200];
        for (var i = 0; i < samples.Length / 2; i++)
        {
            var s = (short)(28_000 * Math.Sin(2 * Math.PI * 440 * i / 16_000.0));
            samples[i * 2] = (byte)(s & 0xFF);
            samples[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        var chunk = new AudioChunk
        {
            Role = AudioStreamRole.Microphone,
            Format = AudioFormat.Mono16kPcm16,
            CapturedAt = DateTimeOffset.UtcNow,
            Samples = samples,
        };

        var segs1 = await transcription.TranscribeAsync(chunk, CancellationToken.None);
        await Task.Delay(5);
        var segs2 = await transcription.TranscribeAsync(chunk, CancellationToken.None);

        foreach (var s in segs1.Concat(segs2)) buffer.Append(s);
        buffer.Snapshot().Should().HaveCountGreaterOrEqualTo(1);
    }
}
