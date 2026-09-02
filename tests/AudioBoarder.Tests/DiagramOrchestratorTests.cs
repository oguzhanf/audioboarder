using AudioBoarder.Core.Layout;
using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services;
using AudioBoarder.Services.Intent;
using AudioBoarder.Tests.Fakes;

namespace AudioBoarder.Tests;

public class DiagramOrchestratorTests
{
    [Fact]
    public async Task LayoutFailureRaisesTerminalFailureEvent()
    {
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(1));
        var orchestrator = new DiagramOrchestrator(
            new InMemoryScenePatchGenerator(),
            new ThrowingLayout(),
            buffer,
            new SceneGraph());
        DiagramGenerationFailed? failure = null;
        orchestrator.GenerationFailed += (_, value) => failure = value;

        var act = () => orchestrator.GenerateAsync(null);

        await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Should().NotBeNull();
        failure!.Error.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task PassesEffectiveNodeBudgetAndExplicitTranscriptWindow()
    {
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(1));
        buffer.Append(new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Local,
            "buffer text", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var generator = new CapturingGenerator();
        var orchestrator = new DiagramOrchestrator(
            generator,
            new NoOpLayout(),
            buffer,
            budget: new SceneBudget(MaxNodes: 7, MaxNotes: 3));
        var explicitWindow = new[]
        {
            new TranscriptSegment(Guid.NewGuid(), TranscriptSpeaker.Remote,
                "explicit text", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        };

        await orchestrator.GenerateAsync(null, transcriptWindow: explicitWindow);

        generator.Request.Should().NotBeNull();
        generator.Request!.MaxNodes.Should().Be(7);
        generator.Request.TranscriptWindow.Should().BeSameAs(explicitWindow);
        generator.Request.GenerationEpoch.Should().Be(generator.Request.CurrentScene.GenerationEpoch);
    }

    [Fact]
    public async Task RestoredFloorDoesNotTurnNegativeNodeCapBackOn()
    {
        var scene = new SceneGraph();
        new ScenePatchApplier().Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "Alpha"),
        }));
        var generator = new CapturingGenerator();
        var orchestrator = new DiagramOrchestrator(
            generator,
            new NoOpLayout(),
            new TranscriptBuffer(TimeSpan.FromMinutes(1)),
            scene,
            budget: new SceneBudget(MaxNodes: -1, MaxNotes: -1));
        orchestrator.RaiseBudgetFloorToCurrentScene();

        await orchestrator.GenerateAsync(null);

        generator.Request!.MaxNodes.Should().Be(-1);
    }

    [Fact]
    public async Task AutoDetectedIntentIsIncludedInGeneratorRequest()
    {
        var buffer = new TranscriptBuffer(TimeSpan.FromMinutes(1));
        var start = DateTimeOffset.UtcNow;
        foreach (var (text, index) in new[]
        {
            "Tenant user signs in to the tenant portal.",
            "The portal passes tenant context to the tenant API.",
            "The API uses row level security in the shared tenant database.",
        }.Select((text, index) => (text, index)))
        {
            buffer.Append(new TranscriptSegment(
                Guid.NewGuid(), TranscriptSpeaker.Remote, text,
                start.AddSeconds(index), start.AddSeconds(index + 1)));
        }
        var generator = new CapturingGenerator();
        var orchestrator = new DiagramOrchestrator(
            generator,
            new NoOpLayout(),
            buffer,
            intentCoordinator: new DiagramIntentCoordinator(new DiagramIntentDetector()));

        await orchestrator.GenerateAsync(null);

        generator.Request!.DiagramIntent.Should().Be(DiagramIntent.SaaSMultiTenantArchitecture);
        generator.Request.Mode.Should().Be(GenerationMode.DeepSynthesis);
        generator.Request.IntentState!.AppliedIntent.Should().Be(DiagramIntent.SaaSMultiTenantArchitecture);
        generator.Request.IntentState.Confidence.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ManualRefineUsesPinnedSelectedIntent()
    {
        var scene = new SceneGraph();
        scene.SetIntentState(new DiagramIntentState(
            DiagramIntent.SecurityZeroTrustArchitecture,
            DiagramIntentSelectionMode.PinnedByUser,
            1,
            "test",
            scene.Revision));
        var generator = new CapturingGenerator();
        var orchestrator = new DiagramOrchestrator(
            generator,
            new NoOpLayout(),
            new TranscriptBuffer(TimeSpan.FromMinutes(1)),
            scene);

        await orchestrator.GenerateAsync(
            "Refine trust boundaries",
            mode: GenerationMode.ManualRefine);

        generator.Request!.Mode.Should().Be(GenerationMode.ManualRefine);
        generator.Request.DiagramIntent.Should().Be(DiagramIntent.SecurityZeroTrustArchitecture);
    }

    [Fact]
    public async Task DeepHttpWorkDoesNotBlockFastGeneration()
    {
        var generator = new OverlapGenerator();
        var orchestrator = new DiagramOrchestrator(
            generator,
            new NoOpLayout(),
            new TranscriptBuffer(TimeSpan.FromMinutes(1)));

        var deep = orchestrator.GenerateAsync(null, mode: GenerationMode.DeepSynthesis);
        await generator.DeepStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var fast = orchestrator.GenerateAsync(
            null,
            mode: GenerationMode.ContinuousExtraction,
            transcriptWindow: Array.Empty<TranscriptSegment>());
        await generator.FastStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fast.WaitAsync(TimeSpan.FromSeconds(2));

        orchestrator.Scene.Nodes["fast"].LifecycleState
            .Should().Be(ElementLifecycleState.Provisional);
        deep.IsCompleted.Should().BeFalse();

        generator.ReleaseDeep.TrySetResult();
        var deepResult = await deep.WaitAsync(TimeSpan.FromSeconds(2));
        orchestrator.Scene.Nodes["deep"].LifecycleState
            .Should().Be(ElementLifecycleState.Confirmed);
        deepResult.StaleDisposition.Should().Be(StalePatchDisposition.MergedSafely);
    }

    [Fact]
    public async Task ContinuousIsProvisionalAndDeepPromotesWithoutOverwritingUserEdits()
    {
        var scene = new SceneGraph();
        var applier = new ScenePatchApplier();
        applier.Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("user", NodeKind.Process, "Curated"),
        }), incomingLifecycle: ElementLifecycleState.Confirmed);
        scene.TryMarkNodeUserEdited("user").Should().BeTrue();

        var generator = new ModeGenerator(request =>
            request.Mode == GenerationMode.ContinuousExtraction
                ? new ScenePatch(new ScenePatchOperation[]
                {
                    new AddNode("live", NodeKind.Process, "Live fact"),
                    new UpdateNode("user", Label: "Model overwrite"),
                })
                : new ScenePatch(Array.Empty<ScenePatchOperation>()));
        var orchestrator = new DiagramOrchestrator(
            generator,
            new NoOpLayout(),
            new TranscriptBuffer(TimeSpan.FromMinutes(1)),
            scene);

        var fast = await orchestrator.GenerateAsync(
            null, mode: GenerationMode.ContinuousExtraction);
        scene.Nodes["live"].LifecycleState.Should().Be(ElementLifecycleState.Provisional);
        scene.Nodes["user"].Label.Should().Be("Curated");
        scene.Nodes["user"].LifecycleState.Should().Be(ElementLifecycleState.UserEdited);
        fast.LifecycleChanges.Should().ContainSingle(x =>
            x.ElementId == "live" && x.Current == ElementLifecycleState.Provisional);

        var deep = await orchestrator.GenerateAsync(null, mode: GenerationMode.DeepSynthesis);
        scene.Nodes["live"].LifecycleState.Should().Be(ElementLifecycleState.Confirmed);
        scene.Nodes["user"].LifecycleState.Should().Be(ElementLifecycleState.UserEdited);
        deep.LifecycleChanges.Should().ContainSingle(x =>
            x.ElementId == "live" &&
            x.Previous == ElementLifecycleState.Provisional &&
            x.Current == ElementLifecycleState.Confirmed);
    }

    [Fact]
    public async Task DeepMayDeleteOnlyProvisionalStructure()
    {
        var scene = new SceneGraph();
        var applier = new ScenePatchApplier();
        applier.Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("provisional", NodeKind.Process, "Provisional"),
        }));
        applier.Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("confirmed", NodeKind.Process, "Confirmed"),
            new AddNode("user", NodeKind.Process, "User"),
        }), incomingLifecycle: ElementLifecycleState.Confirmed);
        scene.TryMarkNodeUserEdited("user").Should().BeTrue();
        var generator = new ModeGenerator(_ => new ScenePatch(new ScenePatchOperation[]
        {
            new DeleteNode("provisional"),
            new DeleteNode("confirmed"),
            new DeleteNode("user"),
        }));
        var orchestrator = new DiagramOrchestrator(
            generator,
            new NoOpLayout(),
            new TranscriptBuffer(TimeSpan.FromMinutes(1)),
            scene);

        var result = await orchestrator.GenerateAsync(null, mode: GenerationMode.DeepSynthesis);

        scene.Nodes.Should().NotContainKey("provisional");
        scene.Nodes.Should().ContainKey("confirmed");
        scene.Nodes.Should().ContainKey("user");
        result.SkippedOperations.Should().Be(2);
    }

    [Fact]
    public async Task ContinuousBudgetTrimNeverEvictsConfirmedContent()
    {
        var scene = new SceneGraph();
        new ScenePatchApplier().Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("confirmed-a", NodeKind.Process, "Confirmed A"),
            new AddNode("confirmed-b", NodeKind.Process, "Confirmed B"),
        }), incomingLifecycle: ElementLifecycleState.Confirmed);
        var generator = new ModeGenerator(_ => new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("new-provisional", NodeKind.Process, "New provisional"),
        }));
        var orchestrator = new DiagramOrchestrator(
            generator,
            new NoOpLayout(),
            new TranscriptBuffer(TimeSpan.FromMinutes(1)),
            scene,
            budget: new SceneBudget(MaxNodes: 2, MaxNotes: 10));

        await orchestrator.GenerateAsync(null, mode: GenerationMode.ContinuousExtraction);

        scene.Nodes.Should().ContainKeys("confirmed-a", "confirmed-b");
        scene.Nodes.Should().NotContainKey("new-provisional");
    }

    [Fact]
    public async Task StaleDeepPatchCannotOverwriteNewUserEdit()
    {
        var scene = new SceneGraph();
        new ScenePatchApplier().Apply(scene, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("target", NodeKind.Process, "Original"),
        }));
        var generator = new DelayedDeepGenerator(new ScenePatch(new ScenePatchOperation[]
        {
            new UpdateNode("target", Label: "Deep label"),
        }));
        var orchestrator = new DiagramOrchestrator(
            generator,
            new NoOpLayout(),
            new TranscriptBuffer(TimeSpan.FromMinutes(1)),
            scene);

        var generation = orchestrator.GenerateAsync(null, mode: GenerationMode.DeepSynthesis);
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scene.TryMarkNodeUserEdited("target").Should().BeTrue();
        generator.Release.TrySetResult();

        var result = await generation.WaitAsync(TimeSpan.FromSeconds(2));
        scene.Nodes["target"].Label.Should().Be("Original");
        scene.Nodes["target"].LifecycleState.Should().Be(ElementLifecycleState.UserEdited);
        result.StaleDisposition.Should().Be(StalePatchDisposition.RejectedStale);
        result.SkippedOperations.Should().Be(1);
    }

    [Fact]
    public async Task EmptyFreshPatchIsAcceptedAsNoChange()
    {
        var orchestrator = new DiagramOrchestrator(
            new CapturingGenerator(),
            new NoOpLayout(),
            new TranscriptBuffer(TimeSpan.FromMinutes(1)));

        var result = await orchestrator.GenerateAsync(
            null, mode: GenerationMode.ContinuousExtraction);

        result.StaleDisposition.Should().Be(StalePatchDisposition.AcceptedNoChanges);
        result.HasSafeApplication.Should().BeTrue();
        result.SafeErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task IdempotentFreshPatchIsAcceptedAsNoChange()
    {
        var scene = new SceneGraph();
        new ScenePatchApplier().Apply(scene, new ScenePatch(
        [
            new NoteUpsert("existing", NoteKind.General, "Same note"),
        ]));
        var generator = new ModeGenerator(_ => new ScenePatch(
        [
            new NoteUpsert("duplicate-id", NoteKind.General, "Same note"),
        ]));
        var orchestrator = new DiagramOrchestrator(
            generator,
            new NoOpLayout(),
            new TranscriptBuffer(TimeSpan.FromMinutes(1)),
            scene);

        var result = await orchestrator.GenerateAsync(
            null, mode: GenerationMode.ContinuousExtraction);

        result.ApplyResult.OperationsApplied.Should().Be(1);
        scene.Notes.Should().ContainSingle();
        result.StaleDisposition.Should().Be(StalePatchDisposition.AcceptedNoChanges);
        result.HasSafeApplication.Should().BeTrue();
    }

    [Fact]
    public async Task ClearRejectsInFlightResponseFromPreviousGenerationEpoch()
    {
        var scene = new SceneGraph();
        new ScenePatchApplier().Apply(scene, new ScenePatch(
        [
            new AddNode("before", NodeKind.Process, "Before clear"),
        ]));
        var generator = new DelayedDeepGenerator(new ScenePatch(
        [
            new AddNode("stale", NodeKind.Process, "Must not return"),
        ]));
        var orchestrator = new DiagramOrchestrator(
            generator,
            new NoOpLayout(),
            new TranscriptBuffer(TimeSpan.FromMinutes(1)),
            scene);

        var generation = orchestrator.GenerateAsync(null);
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var requestEpoch = scene.GenerationEpoch;
        orchestrator.Clear();
        scene.GenerationEpoch.Should().BeGreaterThan(requestEpoch);
        generator.Release.TrySetResult();

        var result = await generation.WaitAsync(TimeSpan.FromSeconds(2));

        scene.Nodes.Should().BeEmpty();
        result.StaleDisposition.Should().Be(StalePatchDisposition.RejectedGenerationEpoch);
        result.HasSafeApplication.Should().BeFalse();
        result.SafeErrorCode.Should().Be("generation_epoch_mismatch");
        result.BaseGenerationEpoch.Should().Be(requestEpoch);
        result.AppliedGenerationEpoch.Should().Be(scene.GenerationEpoch);
        result.Response.Patch.Operations.Should().BeEmpty();
    }

    [Fact]
    public async Task RestoredStateRejectsInFlightResponseFromPreviousGenerationEpoch()
    {
        var scene = new SceneGraph();
        var generator = new DelayedDeepGenerator(new ScenePatch(
        [
            new AddNode("stale", NodeKind.Process, "Must not enter restored board"),
        ]));
        var orchestrator = new DiagramOrchestrator(
            generator,
            new NoOpLayout(),
            new TranscriptBuffer(TimeSpan.FromMinutes(1)),
            scene);

        var generation = orchestrator.GenerateAsync(null);
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var requestEpoch = scene.GenerationEpoch;
        scene.RestorePersistedState(
            [new SceneNode { Id = "restored", Kind = NodeKind.Process, Label = "Restored" }],
            Array.Empty<SceneEdge>(),
            Array.Empty<SceneGroup>(),
            Array.Empty<SceneNote>(),
            Array.Empty<AudioBoarder.Core.Imaging.SceneImage>(),
            revision: 4);
        generator.Release.TrySetResult();

        var result = await generation.WaitAsync(TimeSpan.FromSeconds(2));

        scene.Nodes.Should().ContainSingle();
        scene.Nodes.Should().ContainKey("restored");
        scene.Nodes.Should().NotContainKey("stale");
        scene.GenerationEpoch.Should().BeGreaterThan(requestEpoch);
        result.StaleDisposition.Should().Be(StalePatchDisposition.RejectedGenerationEpoch);
        result.SafeErrorCode.Should().Be("generation_epoch_mismatch");
    }

    [Fact]
    public async Task AppliedIntentChangeRejectsInFlightResponseFromPreviousGenerationEpoch()
    {
        var scene = new SceneGraph();
        var generator = new DelayedDeepGenerator(new ScenePatch(
        [
            new AddNode("old-intent", NodeKind.Process, "Old intent structure"),
        ]));
        var orchestrator = new DiagramOrchestrator(
            generator,
            new NoOpLayout(),
            new TranscriptBuffer(TimeSpan.FromMinutes(1)),
            scene);
        var coordinator = new DiagramIntentCoordinator(new DiagramIntentDetector());

        var generation = orchestrator.GenerateAsync(null);
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var requestEpoch = scene.GenerationEpoch;
        coordinator.Pin(scene, DiagramIntent.DiscussionSummary);
        generator.Release.TrySetResult();

        var result = await generation.WaitAsync(TimeSpan.FromSeconds(2));

        scene.IntentState.AppliedIntent.Should().Be(DiagramIntent.DiscussionSummary);
        scene.Nodes.Should().NotContainKey("old-intent");
        scene.GenerationEpoch.Should().BeGreaterThan(requestEpoch);
        result.StaleDisposition.Should().Be(StalePatchDisposition.RejectedGenerationEpoch);
        result.SafeErrorCode.Should().Be("generation_epoch_mismatch");
    }

    private sealed class ThrowingLayout : ILayoutEngine
    {
        public string Name => "throwing";
        public LayoutResult Apply(SceneGraph graph, LayoutOptions options)
            => throw new InvalidOperationException("layout failed");
    }

    private sealed class NoOpLayout : ILayoutEngine
    {
        public string Name => "no-op";
        public LayoutResult Apply(SceneGraph graph, LayoutOptions options) => new(0, 0, 0);
    }

    private sealed class CapturingGenerator : IScenePatchGenerator
    {
        public string Name => "capture";
        public ScenePatchRequest? Request { get; private set; }
        public Task<ScenePatchResponse> GenerateAsync(ScenePatchRequest request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new ScenePatchResponse(
                new ScenePatch(Array.Empty<ScenePatchOperation>()), Name, TimeSpan.Zero));
        }
    }

    private sealed class ModeGenerator : IScenePatchGenerator
    {
        private readonly Func<ScenePatchRequest, ScenePatch> _create;
        public ModeGenerator(Func<ScenePatchRequest, ScenePatch> create) => _create = create;
        public string Name => "mode";
        public Task<ScenePatchResponse> GenerateAsync(ScenePatchRequest request, CancellationToken ct) =>
            Task.FromResult(new ScenePatchResponse(_create(request), Name, TimeSpan.Zero));
    }

    private sealed class OverlapGenerator : IScenePatchGenerator
    {
        public string Name => "overlap";
        public TaskCompletionSource DeepStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FastStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDeep { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ScenePatchResponse> GenerateAsync(
            ScenePatchRequest request, CancellationToken ct)
        {
            if (request.Mode == GenerationMode.DeepSynthesis)
            {
                DeepStarted.TrySetResult();
                await ReleaseDeep.Task.WaitAsync(ct);
                return new ScenePatchResponse(
                    new ScenePatch(new ScenePatchOperation[]
                    {
                        new AddNode("deep", NodeKind.Process, "Deep"),
                    }),
                    Name,
                    TimeSpan.FromMilliseconds(50));
            }

            FastStarted.TrySetResult();
            return new ScenePatchResponse(
                new ScenePatch(new ScenePatchOperation[]
                {
                    new AddNode("fast", NodeKind.Process, "Fast"),
                }),
                Name,
                TimeSpan.FromMilliseconds(5));
        }
    }

    private sealed class DelayedDeepGenerator : IScenePatchGenerator
    {
        private readonly ScenePatch _patch;
        public DelayedDeepGenerator(ScenePatch patch) => _patch = patch;
        public string Name => "delayed";
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ScenePatchResponse> GenerateAsync(
            ScenePatchRequest request, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new ScenePatchResponse(_patch, Name, TimeSpan.FromMilliseconds(50));
        }
    }
}
