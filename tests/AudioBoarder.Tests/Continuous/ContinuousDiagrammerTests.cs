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
    [Fact]
    public async Task ResetTranscriptProgressDiscardsPreBarrierPendingSpeech()
    {
        var generator = new BlockingFastGenerator();
        var (diagrammer, pipeline, _, buffer) =
            BuildWithGenerator(generator, new SceneGraph());
        diagrammer.Start();

        RaiseSegment(pipeline, "old meeting content");
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        buffer.Clear();
        diagrammer.ResetTranscriptProgress();

        diagrammer.PendingNewSegments.Should().Be(0);
        diagrammer.CommittedCursor.Should().Be(buffer.CurrentCursor);
        await diagrammer.DisposeAsync();
    }

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
        var bufferField = typeof(AudioPipeline).GetField("_buffer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        ((TranscriptBuffer?)bufferField?.GetValue(pipeline))?.Append(seg);
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

    [Fact]
    public async Task FailedBatchRemainsPendingAndRetriesBeforeCursorAdvances()
    {
        var settings = Options.Create(new AudioBoarderSettings
        {
            Realtime = new RealtimeSettings
            {
                Enabled = true,
                MinIntervalSeconds = 0,
                MinNewSegments = 1,
            }
        });
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(5));
        var pipeline = new AudioPipeline(Array.Empty<IAudioCaptureSource>(),
            new ScriptedTranscriptionService(Array.Empty<(TranscriptSpeaker, string)>()),
            new PassThroughVoiceActivityDetector(), buffer);
        var generator = new FailOnceGenerator();
        var orchestrator = new DiagramOrchestrator(generator, new LayeredLayoutEngine(), buffer);
        var diag = new ContinuousDiagrammer(pipeline, orchestrator, settings);
        diag.Start();
        var before = diag.CommittedCursor;

        RaiseSegment(pipeline, "must survive");
        await WaitForAsync(() => diag.Failures == 1, TimeSpan.FromSeconds(2));

        diag.CommittedCursor.Should().Be(before);
        diag.PendingNewSegments.Should().Be(1);

        await WaitForAsync(() => diag.Successes == 1, TimeSpan.FromSeconds(3));
        diag.CommittedCursor.Sequence.Should().BeGreaterThan(before.Sequence);
        generator.Requests.Should().HaveCount(2);
        generator.Requests.Select(r => r.TranscriptWindow.Single().Text)
            .Should().OnlyContain(text => text == "must survive");
        diag.Attempts.Should().Be(2);
        diag.ConsecutiveFailures.Should().Be(0);
        await diag.DisposeAsync();
    }

    [Fact]
    public async Task RejectedStaleFastPatchKeepsCursorAndRetriesLatestSceneWithoutHotLoop()
    {
        var scene = new SceneGraph();
        new ScenePatchApplier().Apply(scene, new ScenePatch(
        [
            new AddNode("target", NodeKind.Process, "Original"),
        ]));
        var generator = new RetryAfterUncommittedGenerator(staleFirst: true);
        var (diagrammer, pipeline, orchestrator, buffer) =
            BuildWithGenerator(generator, scene);
        var results = new List<DiagramGenerationResult>();
        orchestrator.GenerationCompleted += (_, completed) =>
        {
            lock (results) results.Add(completed.Result);
        };
        diagrammer.Start();
        var before = diagrammer.CommittedCursor;

        RaiseSegment(pipeline, "must not be consumed by a stale patch");
        await generator.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scene.TryMarkNodeUserEdited("target").Should().BeTrue();
        generator.ReleaseFirst.TrySetResult();
        await generator.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        diagrammer.CommittedCursor.Should().Be(before);
        generator.Requests[1].CurrentScene.Nodes["target"].LifecycleState
            .Should().Be(ElementLifecycleState.UserEdited);
        (generator.StartedAt[1] - generator.StartedAt[0]).Should()
            .BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200));
        lock (results)
            results[0].StaleDisposition.Should().Be(StalePatchDisposition.RejectedStale);

        generator.ReleaseSecond.TrySetResult();
        await WaitForAsync(() => diagrammer.Successes == 1, TimeSpan.FromSeconds(2));
        diagrammer.CommittedCursor.Should().Be(buffer.CurrentCursor);
        generator.Calls.Should().Be(2);
        await diagrammer.DisposeAsync();
    }

    [Fact]
    public async Task FreshEmptyPatchCommitsCursorWithoutRetryingForever()
    {
        var generator = new RetryAfterUncommittedGenerator(staleFirst: false);
        var (diagrammer, pipeline, orchestrator, buffer) =
            BuildWithGenerator(generator, new SceneGraph());
        var results = new List<DiagramGenerationResult>();
        orchestrator.GenerationCompleted += (_, completed) =>
        {
            lock (results) results.Add(completed.Result);
        };
        diagrammer.Start();
        var before = diagrammer.CommittedCursor;

        RaiseSegment(pipeline, "must survive an empty response");
        await generator.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        generator.ReleaseFirst.TrySetResult();
        await WaitForAsync(
            () => diagrammer.CommittedCursor == buffer.CurrentCursor,
            TimeSpan.FromSeconds(2));

        diagrammer.CommittedCursor.Should().NotBe(before);
        diagrammer.PendingNewSegments.Should().Be(0);
        lock (results)
            results[0].StaleDisposition.Should().Be(StalePatchDisposition.AcceptedNoChanges);
        diagrammer.Successes.Should().Be(1);
        generator.Calls.Should().Be(1);
        await diagrammer.DisposeAsync();
    }

    [Fact]
    public async Task CancellationDoesNotCommitBatchAndRestartRetriesIt()
    {
        var settings = Options.Create(new AudioBoarderSettings
        {
            Realtime = new RealtimeSettings
            {
                Enabled = true,
                MinIntervalSeconds = 0,
                MinNewSegments = 1,
            }
        });
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(5));
        var pipeline = new AudioPipeline(Array.Empty<IAudioCaptureSource>(),
            new ScriptedTranscriptionService(Array.Empty<(TranscriptSpeaker, string)>()),
            new PassThroughVoiceActivityDetector(), buffer);
        var generator = new CancelFirstGenerator();
        var orchestrator = new DiagramOrchestrator(generator, new LayeredLayoutEngine(), buffer);
        var diag = new ContinuousDiagrammer(pipeline, orchestrator, settings);
        diag.Start();
        var before = diag.CommittedCursor;
        RaiseSegment(pipeline, "retry after stop");
        await WaitForAsync(() => generator.Calls >= 1, TimeSpan.FromSeconds(2));

        await diag.StopAsync();
        diag.CommittedCursor.Should().Be(before);
        buffer.ReadAfter(diag.CommittedCursor).Segments.Should().ContainSingle();

        diag.Start();
        await WaitForAsync(() => diag.Successes == 1, TimeSpan.FromSeconds(2));
        generator.Calls.Should().Be(2);
        diag.CommittedCursor.Sequence.Should().BeGreaterThan(before.Sequence);
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
            return new ScenePatchResponse(new ScenePatch(
                [new AddNode($"slow-{CallsStarted}", NodeKind.Process, "Slow result")]),
                Name, _delay, RawJson: null);
        }
    }

    private sealed class FailOnceGenerator : IScenePatchGenerator
    {
        private int _calls;
        public string Name => "fail-once";
        public List<ScenePatchRequest> Requests { get; } = new();

        public Task<ScenePatchResponse> GenerateAsync(ScenePatchRequest request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            if (Interlocked.Increment(ref _calls) == 1)
                throw new HttpRequestException("private body", null, System.Net.HttpStatusCode.ServiceUnavailable);
            return Task.FromResult(new ScenePatchResponse(
                new ScenePatch([new AddNode("recovered", NodeKind.Process, "Recovered")]),
                Name, TimeSpan.Zero));
        }
    }

    private sealed class CancelFirstGenerator : IScenePatchGenerator
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string Name => "cancel-first";

        public async Task<ScenePatchResponse> GenerateAsync(ScenePatchRequest request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ScenePatchResponse(
                new ScenePatch([new AddNode("after-cancel", NodeKind.Process, "After cancel")]),
                Name, TimeSpan.Zero);
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

        await orchestrator.GenerateAsync(null, mode: GenerationMode.ContinuousExtraction);

        scene.Nodes.Should().ContainKey("existing");
        scene.Nodes.Should().ContainKey("safe");
    }

    [Fact]
    public async Task SpeechPauseTriggersDeepWithoutAdditionalSpeech()
    {
        var settings = Options.Create(new AudioBoarderSettings
        {
            Realtime = new RealtimeSettings
            {
                Enabled = true,
                MinIntervalSeconds = 0,
                MinNewSegments = 1,
                DeepPassIntervalSeconds = 0,
                DeepPauseSeconds = 0.08,
            }
        });
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(5));
        var pipeline = new AudioPipeline(Array.Empty<IAudioCaptureSource>(),
            new ScriptedTranscriptionService(Array.Empty<(TranscriptSpeaker, string)>()),
            new PassThroughVoiceActivityDetector(), buffer);
        var generator = new RecordingModeGenerator();
        var scene = new SceneGraph();
        var orchestrator = new DiagramOrchestrator(
            generator, new LayeredLayoutEngine(), buffer, scene);
        var diagrammer = new ContinuousDiagrammer(pipeline, orchestrator, settings);
        diagrammer.Start();

        RaiseSegment(pipeline, "one grounded fact");

        await WaitForAsync(
            () => generator.Modes.Contains(GenerationMode.DeepSynthesis),
            TimeSpan.FromSeconds(3));
        generator.Modes.Should().ContainInOrder(
            GenerationMode.ContinuousExtraction,
            GenerationMode.DeepSynthesis);
        scene.Nodes["pause-fact"].LifecycleState.Should().Be(ElementLifecycleState.Confirmed);
        await diagrammer.DisposeAsync();
    }

    [Fact]
    public async Task MeetingStopTriggersDeepAndCommitsFlushedCursor()
    {
        var settings = Options.Create(new AudioBoarderSettings
        {
            Realtime = new RealtimeSettings
            {
                Enabled = true,
                MinIntervalSeconds = 0,
                MinNewSegments = 1,
                DeepPassIntervalSeconds = 0,
                DeepPauseSeconds = 0,
            }
        });
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(5));
        var pipeline = new AudioPipeline(Array.Empty<IAudioCaptureSource>(),
            new ScriptedTranscriptionService(Array.Empty<(TranscriptSpeaker, string)>()),
            new PassThroughVoiceActivityDetector(), buffer);
        var generator = new RecordingModeGenerator();
        var orchestrator = new DiagramOrchestrator(
            generator, new LayeredLayoutEngine(), buffer);
        var diagrammer = new ContinuousDiagrammer(pipeline, orchestrator, settings);
        diagrammer.Start();
        RaiseSegment(pipeline, "finalized before stop");
        await WaitForAsync(() => diagrammer.Successes >= 1, TimeSpan.FromSeconds(2));

        await diagrammer.StopAsync(synthesizeDeep: true);

        generator.Modes.Should().Contain(GenerationMode.DeepSynthesis);
        diagrammer.CommittedCursor.Should().Be(buffer.CurrentCursor);
    }

    [Fact]
    public async Task FixedTimedDeepPassRemainsOff()
    {
        var settings = Options.Create(new AudioBoarderSettings
        {
            Realtime = new RealtimeSettings
            {
                Enabled = true,
                MinIntervalSeconds = 0,
                MinNewSegments = 1,
                DeepPassIntervalSeconds = 0.01,
                DeepPauseSeconds = 0,
            }
        });
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(5));
        var pipeline = new AudioPipeline(Array.Empty<IAudioCaptureSource>(),
            new ScriptedTranscriptionService(Array.Empty<(TranscriptSpeaker, string)>()),
            new PassThroughVoiceActivityDetector(), buffer);
        var generator = new RecordingModeGenerator();
        var orchestrator = new DiagramOrchestrator(
            generator, new LayeredLayoutEngine(), buffer);
        var diagrammer = new ContinuousDiagrammer(pipeline, orchestrator, settings);
        diagrammer.Start();
        RaiseSegment(pipeline, "only fast");
        await WaitForAsync(() => diagrammer.Successes >= 1, TimeSpan.FromSeconds(2));

        await Task.Delay(150);

        generator.Modes.Should().OnlyContain(x => x == GenerationMode.ContinuousExtraction);
        await diagrammer.DisposeAsync();
    }

    [Fact]
    public async Task RuntimeSnapshotTracksLagAndReturnsToCurrent()
    {
        var settings = Options.Create(new AudioBoarderSettings
        {
            Realtime = new RealtimeSettings
            {
                Enabled = true,
                MinIntervalSeconds = 0,
                MinNewSegments = 1,
                DeepPauseSeconds = 0,
            }
        });
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(5));
        var pipeline = new AudioPipeline(Array.Empty<IAudioCaptureSource>(),
            new ScriptedTranscriptionService(Array.Empty<(TranscriptSpeaker, string)>()),
            new PassThroughVoiceActivityDetector(), buffer);
        var generator = new BlockingFastGenerator();
        var orchestrator = new DiagramOrchestrator(
            generator, new LayeredLayoutEngine(), buffer);
        var diagrammer = new ContinuousDiagrammer(pipeline, orchestrator, settings);
        var observed = new List<ContinuousRuntimeSnapshot>();
        diagrammer.RuntimeChanged += (_, snapshot) =>
        {
            lock (observed) observed.Add(snapshot);
        };
        diagrammer.Start();
        RaiseSegment(pipeline, "lagged");
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var active = diagrammer.RuntimeSnapshot;
        active.Stage.Should().Be(GenerationRuntimeStage.Extracting);
        active.PendingSegments.Should().Be(1);
        active.CurrentCursor.Sequence.Should().BeGreaterThan(active.CommittedCursor.Sequence);
        active.CurrentThroughTimestamp.Should().NotBeNull();
        active.Lag.Should().BeGreaterOrEqualTo(TimeSpan.Zero);

        generator.Release.TrySetResult();
        await WaitForAsync(
            () => diagrammer.RuntimeSnapshot.Stage == GenerationRuntimeStage.Current,
            TimeSpan.FromSeconds(2));
        diagrammer.RuntimeSnapshot.PendingSegments.Should().Be(0);
        lock (observed)
            observed.Should().Contain(x => x.Stage == GenerationRuntimeStage.Extracting);
        await diagrammer.DisposeAsync();
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

    private sealed class RecordingModeGenerator : IScenePatchGenerator
    {
        private readonly object _gate = new();
        private readonly List<GenerationMode> _modes = new();
        public string Name => "recording";
        public IReadOnlyList<GenerationMode> Modes
        {
            get { lock (_gate) return _modes.ToArray(); }
        }

        public Task<ScenePatchResponse> GenerateAsync(ScenePatchRequest request, CancellationToken ct)
        {
            lock (_gate) _modes.Add(request.Mode);
            var patch = request.Mode == GenerationMode.ContinuousExtraction
                ? new ScenePatch(new ScenePatchOperation[]
                {
                    new AddNode("pause-fact", NodeKind.Process, "Pause fact"),
                })
                : new ScenePatch(Array.Empty<ScenePatchOperation>());
            return Task.FromResult(new ScenePatchResponse(patch, Name, TimeSpan.Zero));
        }
    }

    private sealed class BlockingFastGenerator : IScenePatchGenerator
    {
        public string Name => "blocking-fast";
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ScenePatchResponse> GenerateAsync(
            ScenePatchRequest request, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new ScenePatchResponse(
                new ScenePatch([new AddNode("lagged", NodeKind.Process, "Lagged")]),
                Name,
                TimeSpan.FromMilliseconds(20));
        }
    }

    private static (ContinuousDiagrammer Diagrammer, AudioPipeline Pipeline,
        DiagramOrchestrator Orchestrator, TranscriptBuffer Buffer) BuildWithGenerator(
        IScenePatchGenerator generator,
        SceneGraph scene)
    {
        var settings = Options.Create(new AudioBoarderSettings
        {
            Realtime = new RealtimeSettings
            {
                Enabled = true,
                MinIntervalSeconds = 0,
                MinNewSegments = 1,
                DeepPauseSeconds = 0,
            }
        });
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(5));
        var pipeline = new AudioPipeline(
            Array.Empty<IAudioCaptureSource>(),
            new ScriptedTranscriptionService(Array.Empty<(TranscriptSpeaker, string)>()),
            new PassThroughVoiceActivityDetector(),
            buffer);
        var orchestrator = new DiagramOrchestrator(
            generator, new LayeredLayoutEngine(), buffer, scene);
        return (new ContinuousDiagrammer(pipeline, orchestrator, settings),
            pipeline, orchestrator, buffer);
    }

    private sealed class RetryAfterUncommittedGenerator(bool staleFirst) : IScenePatchGenerator
    {
        private int _calls;
        private readonly List<ScenePatchRequest> _requests = new();
        private readonly List<DateTimeOffset> _startedAt = new();

        public string Name => "retry-after-uncommitted";
        public int Calls => Volatile.Read(ref _calls);
        public IReadOnlyList<ScenePatchRequest> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }
        public IReadOnlyList<DateTimeOffset> StartedAt
        {
            get { lock (_requests) return _startedAt.ToArray(); }
        }
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ScenePatchResponse> GenerateAsync(
            ScenePatchRequest request, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            lock (_requests)
            {
                _requests.Add(request);
                _startedAt.Add(DateTimeOffset.UtcNow);
            }

            if (call == 1)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(ct);
                var firstPatch = staleFirst
                    ? new ScenePatch([new UpdateNode("target", Label: "Stale overwrite")])
                    : ScenePatch.Empty;
                return new ScenePatchResponse(firstPatch, Name, TimeSpan.Zero);
            }

            if (call == 2)
            {
                SecondStarted.TrySetResult();
                await ReleaseSecond.Task.WaitAsync(ct);
            }

            return new ScenePatchResponse(
                new ScenePatch([new AddNode("accepted", NodeKind.Process, "Accepted")]),
                Name,
                TimeSpan.Zero);
        }
    }
}
