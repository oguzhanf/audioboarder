using AudioBoarder.App.Configuration;
using AudioBoarder.App.Continuous;
using AudioBoarder.Core.Audio;
using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services;
using AudioBoarder.Services.Audio;
using AudioBoarder.Services.Layout;
using AudioBoarder.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace AudioBoarder.Tests.Continuous;

public class ContinuousDiagrammerTests
{
    private static (ContinuousDiagrammer diag, AudioPipeline pipeline, DiagramOrchestrator orch,
        InMemoryScenePatchGenerator gen, TranscriptBuffer buffer) Build(double interval = 0.05, int minSegs = 2)
    {
        var settings = Options.Create(new AudioBoarderSettings
        {
            Realtime = new RealtimeSettings
            {
                Enabled = true,
                MinIntervalSeconds = interval,
                MinNewSegments = minSegs,
            }
        });

        var scene = new SceneGraph();
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(5));
        var transcription = new ScriptedTranscriptionService(Array.Empty<(TranscriptSpeaker, string)>());
        var vad = new PassThroughVoiceActivityDetector();
        var pipeline = new AudioPipeline(Array.Empty<IAudioCaptureSource>(), transcription, vad, buffer);
        var gen = new InMemoryScenePatchGenerator();
        var orch = new DiagramOrchestrator(gen, new LayeredLayoutEngine(), buffer, scene);
        var diag = new ContinuousDiagrammer(pipeline, orch, settings);
        return (diag, pipeline, orch, gen, buffer);
    }

    private static void RaiseSegment(AudioPipeline pipeline, string text)
    {
        var seg = new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Local, text,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        // Use reflection to invoke the SegmentEmitted event since pipeline gates it via internal pump.
        var ev = typeof(AudioPipeline).GetField("SegmentEmitted",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var handler = (EventHandler<TranscriptSegment>?)ev?.GetValue(pipeline);
        handler?.Invoke(pipeline, seg);
    }

    [Fact]
    public async Task TriggersAfterMinSegments()
    {
        var (diag, pipeline, _, _, _) = Build(interval: 0.0, minSegs: 3);
        diag.Start();

        RaiseSegment(pipeline, "one");
        RaiseSegment(pipeline, "two");
        await Task.Delay(80);
        diag.TotalGenerations.Should().Be(0, "fewer than minSegs new segments");

        RaiseSegment(pipeline, "three");
        await WaitForAsync(() => diag.TotalGenerations >= 1, TimeSpan.FromSeconds(2));
        diag.TotalGenerations.Should().BeGreaterOrEqualTo(1);
        await diag.DisposeAsync();
    }

    [Fact]
    public async Task RespectsMinInterval()
    {
        var (diag, pipeline, _, _, _) = Build(interval: 1.0, minSegs: 1);
        diag.Start();

        RaiseSegment(pipeline, "one");
        await WaitForAsync(() => diag.TotalGenerations >= 1, TimeSpan.FromSeconds(2));
        var firstCount = diag.TotalGenerations;

        // Another segment immediately — should NOT trigger because interval gate
        RaiseSegment(pipeline, "two");
        await Task.Delay(200);
        diag.TotalGenerations.Should().Be(firstCount, "interval gate should hold");

        await diag.DisposeAsync();
    }

    [Fact]
    public async Task PendingSegmentsWakeWhenIntervalExpiresWithoutMoreSpeech()
    {
        var (diag, pipeline, _, _, _) = Build(interval: 0.25, minSegs: 1);
        diag.Start();

        RaiseSegment(pipeline, "first");
        await WaitForAsync(() => diag.TotalGenerations >= 1, TimeSpan.FromSeconds(2));

        RaiseSegment(pipeline, "second");
        await Task.Delay(100);
        diag.TotalGenerations.Should().Be(1, "the cooldown is still active");

        await WaitForAsync(() => diag.TotalGenerations >= 2, TimeSpan.FromSeconds(2));
        diag.TotalGenerations.Should().Be(2,
            "pending speech must schedule its own wake-up instead of waiting for another segment");

        await diag.DisposeAsync();
    }

    [Fact]
    public async Task QueuesFollowupWhenSegmentsArriveDuringGeneration()
    {
        // Use a slow generator so the first generation is genuinely "in flight" while
        // the next segments arrive (otherwise they get snapshotted by the first call).
        var settings = Options.Create(new AudioBoarderSettings
        {
            Realtime = new RealtimeSettings { Enabled = true, MinIntervalSeconds = 0, MinNewSegments = 1 }
        });
        var scene = new SceneGraph();
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(5));
        var pipeline = new AudioPipeline(Array.Empty<IAudioCaptureSource>(),
            new ScriptedTranscriptionService(Array.Empty<(TranscriptSpeaker, string)>()),
            new PassThroughVoiceActivityDetector(), buffer);
        var slowGen = new SlowInMemoryGenerator(TimeSpan.FromMilliseconds(300));
        var orch = new DiagramOrchestrator(slowGen, new LayeredLayoutEngine(), buffer, scene);
        var diag = new ContinuousDiagrammer(pipeline, orch, settings);
        diag.Start();

        RaiseSegment(pipeline, "first");
        // Wait until generation has actually entered the slow path
        await WaitForAsync(() => slowGen.CallsStarted >= 1, TimeSpan.FromSeconds(2));
        // Now send more segments while the in-flight call is still running
        for (var i = 0; i < 5; i++) RaiseSegment(pipeline, $"during-{i}");

        await WaitForAsync(() => diag.TotalGenerations >= 2, TimeSpan.FromSeconds(5));
        diag.TotalGenerations.Should().BeGreaterOrEqualTo(2,
            "segments arriving during an in-flight call should queue a follow-up generation");
        await diag.DisposeAsync();
    }

    private sealed class SlowInMemoryGenerator : IScenePatchGenerator
    {
        private readonly TimeSpan _delay;
        private int _started;
        public SlowInMemoryGenerator(TimeSpan delay) { _delay = delay; }
        public string Name => "Slow";
        public int CallsStarted => Volatile.Read(ref _started);
        public async Task<ScenePatchResponse> GenerateAsync(ScenePatchRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _started);
            await Task.Delay(_delay, ct).ConfigureAwait(false);
            return new ScenePatchResponse(new ScenePatch(Array.Empty<ScenePatchOperation>()),
                Name, _delay, RawJson: null);
        }
    }

    [Fact]
    public async Task DisabledFlagSkipsStart()
    {
        var settings = Options.Create(new AudioBoarderSettings
        {
            Realtime = new RealtimeSettings { Enabled = false, MinIntervalSeconds = 0, MinNewSegments = 1 }
        });
        var scene = new SceneGraph();
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(1));
        var pipeline = new AudioPipeline(Array.Empty<IAudioCaptureSource>(),
            new ScriptedTranscriptionService(Array.Empty<(TranscriptSpeaker, string)>()),
            new PassThroughVoiceActivityDetector(), buffer);
        var orch = new DiagramOrchestrator(new InMemoryScenePatchGenerator(), new LayeredLayoutEngine(), buffer, scene);
        var diag = new ContinuousDiagrammer(pipeline, orch, settings);

        diag.Start();
        diag.IsRunning.Should().BeFalse();

        RaiseSegment(pipeline, "x");
        await Task.Delay(100);
        diag.TotalGenerations.Should().Be(0);
        await diag.DisposeAsync();
    }

    [Fact]
    public async Task ContinuousGenerationRejectsDestructiveOperations()
    {
        var scene = new SceneGraph();
        new ScenePatchApplier().Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("existing", NodeKind.Process, "Keep me"),
        }));
        var generator = new DestructiveGenerator();
        var orchestrator = new DiagramOrchestrator(
            generator,
            new LayeredLayoutEngine(),
            new TranscriptBuffer(TimeSpan.FromMinutes(1)),
            scene);

        await orchestrator.GenerateAsync(null, isContinuous: true);

        scene.Nodes.Should().ContainKey("existing");
        scene.Nodes.Should().ContainKey("safe");
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && sw.Elapsed < timeout)
            await Task.Delay(20);
    }

    private sealed class DestructiveGenerator : IScenePatchGenerator
    {
        public string Name => "destructive-test";
        public Task<ScenePatchResponse> GenerateAsync(ScenePatchRequest request, CancellationToken ct)
        {
            var patch = new ScenePatch(new ScenePatchOperation[]
            {
                new ClearScene(),
                new DeleteNode("existing"),
                new AddNode("safe", NodeKind.Process, "Safe update"),
            });
            return Task.FromResult(new ScenePatchResponse(patch, Name, TimeSpan.Zero));
        }
    }
}
