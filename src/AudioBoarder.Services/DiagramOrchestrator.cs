using System.Diagnostics;
using System.Text;
using AudioBoarder.Core.Imaging;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.Intent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioBoarder.Services;

/// <summary>
/// High-level coordinator the UI calls when the user clicks "Diagram now"
/// or "Refine". Builds a request from the current buffer, calls the LLM,
/// applies the patch atomically, runs layout, raises events. Also fires
/// any <see cref="GenerateImage"/> ops the LLM emits as parallel
/// background image-generation tasks.
/// </summary>
public sealed class DiagramOrchestrator : IAsyncDisposable
{
    private readonly IScenePatchGenerator _generator;
    private readonly ScenePatchApplier _applier;
    private readonly ILayoutEngine _layout;
    private readonly TranscriptBuffer _buffer;
    private readonly IImageGenerator? _imageGenerator;
    private readonly ILogger<DiagramOrchestrator> _logger;
    private readonly SceneBudget _budget;
    private readonly DiagramIntentCoordinator _intentCoordinator;
    private int _restoredNodeFloor;
    private int _restoredNoteFloor;
    private readonly SemaphoreSlim _fastGate = new(1, 1);
    private readonly SemaphoreSlim _deepGate = new(1, 1);
    private readonly object _runtimeGate = new();
    private GenerationRuntimeSnapshot _runtime = GenerationRuntimeSnapshot.Idle;
    private int _fastInFlight;
    private int _deepInFlight;
    private readonly SemaphoreSlim _imageConcurrency = new(2, 2);
    private readonly object _imageTaskGate = new();
    private readonly HashSet<Task> _imageTasks = new();
    private CancellationTokenSource _imageCts = new();

    public SceneGraph Scene { get; }
    public TranscriptBuffer TranscriptBuffer => _buffer;

    /// <summary>
    /// How much recent transcript a continuous pass sees. Long enough to carry a
    /// complete thought, short enough that the prompt does not grow with the meeting.
    /// </summary>
    public static readonly TimeSpan ContinuousTranscriptWindow = TimeSpan.FromSeconds(75);

    public event EventHandler<DiagramGenerationStarted>? GenerationStarted;
    public event EventHandler<DiagramGenerationCompleted>? GenerationCompleted;
    public event EventHandler<DiagramGenerationFailed>? GenerationFailed;
    public event EventHandler<SceneImageUpdated>? ImageUpdated;
    public event EventHandler<GenerationRuntimeSnapshot>? RuntimeChanged;

    public GenerationRuntimeSnapshot RuntimeSnapshot
    {
        get { lock (_runtimeGate) return _runtime; }
    }

    public DiagramOrchestrator(
        IScenePatchGenerator generator,
        ILayoutEngine layout,
        TranscriptBuffer buffer,
        SceneGraph? scene = null,
        IImageGenerator? imageGenerator = null,
        ILogger<DiagramOrchestrator>? logger = null,
        SceneBudget? budget = null,
        DiagramIntentCoordinator? intentCoordinator = null)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _imageGenerator = imageGenerator;
        _applier = new ScenePatchApplier();
        _logger = logger ?? NullLogger<DiagramOrchestrator>.Instance;
        _budget = budget ?? SceneBudget.Default;
        _intentCoordinator = intentCoordinator ??
            new DiagramIntentCoordinator(new DiagramIntentDetector());
        Scene = scene ?? new SceneGraph();
    }

    public bool SupportsImages => _imageGenerator is { IsConfigured: true };

    /// <summary>
    /// Raises the budget floor so content the user explicitly restored from a prior
    /// session is never silently evicted. The cap still bounds further growth, but a
    /// restored board keeps everything the user said yes to.
    /// </summary>
    public void RaiseBudgetFloorToCurrentScene()
    {
        lock (Scene.SyncRoot)
        {
            _restoredNodeFloor = Math.Max(_restoredNodeFloor, Scene.Nodes.Count);
            _restoredNoteFloor = Math.Max(_restoredNoteFloor, Scene.Notes.Count);
        }
    }

    private SceneBudget EffectiveBudget() => _restoredNodeFloor == 0 && _restoredNoteFloor == 0
        ? _budget
        : new SceneBudget(
            _budget.MaxNodes < 0 ? _budget.MaxNodes : Math.Max(_budget.MaxNodes, _restoredNodeFloor),
            _budget.MaxNotes < 0 ? _budget.MaxNotes : Math.Max(_budget.MaxNotes, _restoredNoteFloor));

    /// <summary>
    /// Re-sizes every node to its text and re-runs layout without calling the model.
    /// Used after restoring a session, whose persisted geometry may predate the
    /// current sizing and layout rules.
    /// </summary>
    public void Relayout(LayoutOptions? layoutOptions = null) =>
        ReflowUnpinned(layoutOptions);

    public void ReflowUnpinned(LayoutOptions? layoutOptions = null)
    {
        lock (Scene.SyncRoot)
        {
            NodeSizer.ApplyTo(Scene);
            _layout.Apply(Scene, (layoutOptions ?? new LayoutOptions()) with { ReflowPinned = false });
            Scene.NotifyGeometryChanged();
        }
    }

    public void ReflowAll(LayoutOptions? layoutOptions = null)
    {
        lock (Scene.SyncRoot)
        {
            foreach (var node in Scene.Nodes.Values)
            {
                var (width, height) = NodeSizer.Measure(
                    node.Label, node.Description, hasIcon: true, kind: node.Kind);
                node.Width = width;
                node.Height = height;
            }
            _layout.Apply(Scene, (layoutOptions ?? new LayoutOptions()) with { ReflowPinned = true });
            Scene.NotifyGeometryChanged();
        }
    }

    public async Task<DiagramGenerationResult> GenerateAsync(
        string? userInstruction,
        LayoutOptions? layoutOptions = null,
        GenerationMode mode = GenerationMode.DeepSynthesis,
        IReadOnlyList<TranscriptSegment>? transcriptWindow = null,
        CancellationToken ct = default)
    {
        var gate = mode == GenerationMode.ContinuousExtraction ? _fastGate : _deepGate;
        PublishRuntime(GenerationRuntimeStage.Queued, mode, null);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var succeeded = false;
        try
        {
            Notify(GenerationStarted,
                new DiagramGenerationStarted(_generator.Name, userInstruction, mode), "started");
            BeginInFlight(mode);

            var effectiveBudget = EffectiveBudget();
            // Intent detection always considers finalized transcript accumulated so
            // far, even when the model receives only a small continuous delta.
            _intentCoordinator.Evaluate(Scene, _buffer.Snapshot());
            var snapshot = Scene.Clone();
            var baseRevision = snapshot.Revision;
            var baseGenerationEpoch = snapshot.GenerationEpoch;
            var intentState = snapshot.IntentState;
            var request = new ScenePatchRequest(
                CurrentScene: snapshot,
                // Continuous passes get only what was just said. The scene already
                // encodes everything earlier, so re-sending the whole rolling window
                // every few seconds only inflates the prompt (and the latency) as the
                // meeting goes on. Deep passes still see the full window.
                TranscriptWindow: transcriptWindow ?? (mode == GenerationMode.ContinuousExtraction
                    ? _buffer.SnapshotRecent(ContinuousTranscriptWindow)
                    : _buffer.Snapshot()),
                UserInstruction: userInstruction,
                MaxNodes: effectiveBudget.MaxNodes,
                Mode: mode,
                DiagramIntent: intentState.AppliedIntent,
                IntentState: intentState,
                GenerationEpoch: baseGenerationEpoch);

            PublishRuntime(
                mode == GenerationMode.ContinuousExtraction
                    ? GenerationRuntimeStage.Extracting
                    : GenerationRuntimeStage.DeepSynthesizing,
                mode,
                null,
                baseRevision);
            var response = await _generator.GenerateAsync(request, ct).ConfigureAwait(false);

            // Apply the patch and run layout as ONE critical section against the
            // same lock the renderer uses, so the UI thread never paints a
            // half-mutated graph. The applier is best-effort (skips bad ops) so
            // it won't throw on imperfect LLM output.
            ScenePatchResult applyResult;
            LayoutResult layoutResult;
            SceneBudgetResult budgetResult;
            IReadOnlyList<ElementLifecycleChange> lifecycleChanges;
            StalePatchDisposition disposition;
            string? safeErrorCode;
            int appliedRevision;
            long appliedGenerationEpoch;
            lock (Scene.SyncRoot)
            {
                var generatedOperationCount = response.Patch.Operations.Count;
                if (Scene.GenerationEpoch != baseGenerationEpoch)
                {
                    response = response with { Patch = ScenePatch.Empty };
                    applyResult = new ScenePatchResult(
                        0, Scene.Revision, generatedOperationCount);
                    layoutResult = new LayoutResult(0, 0, 0);
                    budgetResult = SceneBudgetResult.Empty;
                    lifecycleChanges = Array.Empty<ElementLifecycleChange>();
                    disposition = StalePatchDisposition.RejectedGenerationEpoch;
                    safeErrorCode = "generation_epoch_mismatch";
                    appliedRevision = Scene.Revision;
                    appliedGenerationEpoch = Scene.GenerationEpoch;
                }
                else
                {
                // Filter inside the same lock as the apply: reading the locked-node
                // set outside it leaves a window where a drag completing mid-flight
                // would not be seen, and the node the user just pinned gets deleted.
                var stale = Scene.Revision != baseRevision;
                var lifecycleBefore = CaptureLifecycle(Scene);
                var promotableNodes = snapshot.Nodes.Values
                    .Where(x => x.LifecycleState == ElementLifecycleState.Provisional &&
                                Scene.Nodes.TryGetValue(x.Id, out var live) &&
                                (!stale || SameNode(x, live)))
                    .Select(x => x.Id)
                    .ToArray();
                var promotableEdges = snapshot.Edges.Values
                    .Where(x => x.LifecycleState == ElementLifecycleState.Provisional &&
                                Scene.Edges.TryGetValue(x.Id, out var live) &&
                                (!stale || SameEdge(x, live)))
                    .Select(x => x.Id)
                    .ToArray();
                var promotableGroups = snapshot.Groups.Values
                    .Where(x => x.LifecycleState == ElementLifecycleState.Provisional &&
                                Scene.Groups.TryGetValue(x.Id, out var live) &&
                                (!stale || SameGroup(x, live)))
                    .Select(x => x.Id)
                    .ToArray();
                var filtered = FilterGeneratedPatch(
                    response.Patch, Scene, snapshot, mode, stale);
                var safePatch = filtered.Patch;
                response = response with { Patch = safePatch };

                var revisionBeforeApply = Scene.Revision;
                applyResult = _applier.Apply(
                    Scene,
                    safePatch,
                    allowClear: false,
                    incomingLifecycle: mode == GenerationMode.ContinuousExtraction
                        ? ElementLifecycleState.Provisional
                        : ElementLifecycleState.Confirmed);
                applyResult = applyResult with
                {
                    OperationsSkipped = applyResult.OperationsSkipped + filtered.OperationsSkipped,
                };
                if (mode != GenerationMode.ContinuousExtraction)
                    Scene.PromoteProvisionalElements(
                        promotableNodes, promotableEdges, promotableGroups);
                var hasSafeApplication = Scene.Revision != revisionBeforeApply;
                var acceptedNoChanges =
                    !stale &&
                    !hasSafeApplication &&
                    applyResult.OperationsSkipped == 0;
                if (hasSafeApplication)
                {
                    // Trim before layout so positions are only computed for what survives.
                    budgetResult = SceneBudgetEnforcer.Enforce(
                        Scene,
                        effectiveBudget,
                        provisionalOnly: true);
                    // Size every box to its own text BEFORE layout, so the footprint the
                    // layout engine reserves matches what actually gets drawn.
                    NodeSizer.ApplyTo(Scene);
                    layoutResult = _layout.Apply(Scene, layoutOptions ?? new LayoutOptions());
                }
                else
                {
                    budgetResult = SceneBudgetResult.Empty;
                    layoutResult = new LayoutResult(0, 0, 0);
                }
                lifecycleChanges = CompareLifecycle(lifecycleBefore, CaptureLifecycle(Scene));
                disposition = stale
                    ? hasSafeApplication
                        ? StalePatchDisposition.MergedSafely
                        : StalePatchDisposition.RejectedStale
                    : hasSafeApplication
                        ? StalePatchDisposition.Fresh
                        : acceptedNoChanges
                            ? StalePatchDisposition.AcceptedNoChanges
                            : StalePatchDisposition.NoSafeApplication;
                safeErrorCode = disposition switch
                {
                    StalePatchDisposition.RejectedStale => "stale_patch_discarded",
                    StalePatchDisposition.NoSafeApplication => "no_safe_application",
                    _ => null,
                };
                appliedRevision = Scene.Revision;
                appliedGenerationEpoch = Scene.GenerationEpoch;
                }
            }

            if (budgetResult.ChangedAnything)
            {
                _logger.LogInformation(
                    "Scene budget trimmed the board: {Nodes} node(s), {Notes} note(s), {Groups} empty group(s)",
                    budgetResult.NodesEvicted, budgetResult.NotesEvicted, budgetResult.GroupsRemoved);
            }
            if (!budgetResult.IsWithinBudget)
            {
                _logger.LogWarning(
                    "Scene remains over budget after safe trimming; nodeOverage={NodeOverage} noteOverage={NoteOverage}",
                    budgetResult.RemainingNodeOverage, budgetResult.RemainingNoteOverage);
            }

            var imageOps = response.Patch.Operations.OfType<GenerateImage>().ToList();
            var result = new DiagramGenerationResult(
                response,
                applyResult,
                layoutResult,
                imageOps.Count,
                budgetResult,
                baseRevision,
                appliedRevision,
                lifecycleChanges,
                applyResult.OperationsSkipped,
                disposition,
                baseGenerationEpoch,
                appliedGenerationEpoch,
                safeErrorCode);
            Notify(GenerationCompleted, new DiagramGenerationCompleted(result), "completed");

            // Fire image generations in the background — never block the diagram flow on image latency.
            foreach (var op in imageOps)
                QueueImageGeneration(op);
            succeeded = true;
            PublishRuntime(
                result.HasSafeApplication
                    ? GenerationRuntimeStage.Current
                    : GenerationRuntimeStage.Degraded,
                mode,
                result.SafeErrorCode,
                baseRevision,
                appliedRevision);
            return result;
        }
        catch (OperationCanceledException ex)
        {
            Notify(GenerationFailed, new DiagramGenerationFailed(ex), "failed");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Diagram generation failed; category={Category}",
                ex is HttpRequestException ? "model_request_failure" : "generation_failure");
            Notify(GenerationFailed, new DiagramGenerationFailed(ex), "failed");
            PublishRuntime(GenerationRuntimeStage.Error, mode, SafeErrorCode(ex));
            throw;
        }
        finally
        {
            EndInFlight(mode, succeeded);
            gate.Release();
        }
    }

    private static PatchFilterResult FilterGeneratedPatch(
        ScenePatch patch,
        SceneGraph scene,
        SceneGraph baseScene,
        GenerationMode mode,
        bool stale)
    {
        // Caller holds Scene.SyncRoot.
        var lockedNodeIds = scene.Nodes.Values
            .Where(n => n.Locked)
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

        var safe = patch.Operations
            .Where(op => IsAllowed(op, scene, baseScene, lockedNodeIds, mode, stale))
            .ToArray();
        return new PatchFilterResult(
            safe.Length == patch.Operations.Count ? patch : new ScenePatch(safe),
            patch.Operations.Count - safe.Length);
    }

    private static bool IsAllowed(
        ScenePatchOperation op,
        SceneGraph scene,
        SceneGraph baseScene,
        HashSet<string> lockedNodeIds,
        GenerationMode mode,
        bool stale)
    {
        // Never honour a wholesale wipe from the model; Clear is a user action.
        if (op is ClearScene) return false;

        // Protect user-pinned nodes from deletion on any pass.
        if (op is DeleteNode delete && lockedNodeIds.Contains(delete.Id)) return false;
        if (TargetsUserEdited(scene, op)) return false;

        // Quick continuous passes are purely additive: they run every few seconds, so
        // a single mis-fire must never be able to remove anything.
        if (mode == GenerationMode.ContinuousExtraction &&
            op is DeleteNode or Disconnect or UngroupOp or NoteDelete or DeleteImage)
        {
            return false;
        }

        // Model-driven passes may prune only unsupported provisional structure.
        // Notes and images do not have lifecycle state and are therefore never
        // eligible for automatic/model deletion.
        if (op is NoteDelete or DeleteImage) return false;
        if (op is DeleteNode deleteNode)
        {
            if (!scene.Nodes.TryGetValue(deleteNode.Id, out var node) ||
                node.LifecycleState != ElementLifecycleState.Provisional)
                return false;
            if (scene.Edges.Values.Any(edge =>
                    (edge.FromNodeId == deleteNode.Id || edge.ToNodeId == deleteNode.Id) &&
                    edge.LifecycleState != ElementLifecycleState.Provisional))
                return false;
        }
        if (op is Disconnect disconnect &&
            (!scene.Edges.TryGetValue(disconnect.Id, out var edge) ||
             edge.LifecycleState != ElementLifecycleState.Provisional))
            return false;
        if (op is UngroupOp ungroup &&
            (!scene.Groups.TryGetValue(ungroup.Id, out var group) ||
             group.LifecycleState != ElementLifecycleState.Provisional ||
             scene.Nodes.Values.Any(n => n.GroupId == ungroup.Id &&
                                         n.LifecycleState == ElementLifecycleState.UserEdited)))
            return false;

        if (stale && HasTargetChangedSince(baseScene, scene, op)) return false;

        return true;
    }

    private static bool TargetsUserEdited(SceneGraph scene, ScenePatchOperation op) => op switch
    {
        AddNode add => scene.Nodes.TryGetValue(add.Id, out var addNode) &&
                       addNode.LifecycleState == ElementLifecycleState.UserEdited,
        UpdateNode update => scene.Nodes.TryGetValue(update.Id, out var updateNode) &&
                             updateNode.LifecycleState == ElementLifecycleState.UserEdited,
        DeleteNode delete => scene.Nodes.TryGetValue(delete.Id, out var deleteNode) &&
                             deleteNode.LifecycleState == ElementLifecycleState.UserEdited,
        Connect connect => scene.Edges.TryGetValue(connect.Id, out var connectEdge) &&
                           connectEdge.LifecycleState == ElementLifecycleState.UserEdited,
        Disconnect disconnect => scene.Edges.TryGetValue(disconnect.Id, out var disconnectEdge) &&
                                 disconnectEdge.LifecycleState == ElementLifecycleState.UserEdited,
        Relabel relabel =>
            scene.Nodes.TryGetValue(relabel.Id, out var relabelNode) &&
            relabelNode.LifecycleState == ElementLifecycleState.UserEdited ||
            scene.Edges.TryGetValue(relabel.Id, out var relabelEdge) &&
            relabelEdge.LifecycleState == ElementLifecycleState.UserEdited ||
            scene.Groups.TryGetValue(relabel.Id, out var relabelGroup) &&
            relabelGroup.LifecycleState == ElementLifecycleState.UserEdited,
        GroupOp group => scene.Groups.TryGetValue(group.Id, out var existingGroup) &&
                         existingGroup.LifecycleState == ElementLifecycleState.UserEdited,
        UngroupOp ungroup => scene.Groups.TryGetValue(ungroup.Id, out var ungroupGroup) &&
                             ungroupGroup.LifecycleState == ElementLifecycleState.UserEdited,
        _ => false,
    };

    private static bool HasTargetChangedSince(
        SceneGraph baseScene, SceneGraph currentScene, ScenePatchOperation op)
    {
        return op switch
        {
            AddNode add => NodeTargetChanged(baseScene, currentScene, add.Id, add.Label),
            UpdateNode update => NodeTargetChanged(baseScene, currentScene, update.Id, null),
            DeleteNode delete => NodeTargetChanged(baseScene, currentScene, delete.Id, null),
            Relabel relabel => ElementTargetChanged(baseScene, currentScene, relabel.Id),
            Connect connect => EdgeTargetChanged(baseScene, currentScene, connect.Id),
            Disconnect disconnect => EdgeTargetChanged(baseScene, currentScene, disconnect.Id),
            GroupOp group => GroupTargetChanged(baseScene, currentScene, group.Id),
            UngroupOp ungroup => GroupTargetChanged(baseScene, currentScene, ungroup.Id),
            _ => false,
        };
    }

    private static bool NodeTargetChanged(
        SceneGraph baseScene, SceneGraph currentScene, string id, string? addLabel)
    {
        var hadBase = baseScene.Nodes.TryGetValue(id, out var before);
        var hasCurrent = currentScene.Nodes.TryGetValue(id, out var current);
        if (hadBase != hasCurrent) return true;
        if (hadBase && !SameNode(before!, current!)) return true;
        if (hadBase || string.IsNullOrWhiteSpace(addLabel)) return false;

        var key = NormalizeIdentity(addLabel);
        var currentByLabel = currentScene.Nodes.Values.FirstOrDefault(
            n => NormalizeIdentity(n.Label) == key);
        if (currentByLabel is null) return false;
        var baseByLabel = baseScene.Nodes.Values.FirstOrDefault(
            n => NormalizeIdentity(n.Label) == key);
        return baseByLabel is null || !SameNode(baseByLabel, currentByLabel);
    }

    private static bool EdgeTargetChanged(SceneGraph before, SceneGraph current, string id)
    {
        var hadBase = before.Edges.TryGetValue(id, out var a);
        var hasCurrent = current.Edges.TryGetValue(id, out var b);
        return hadBase != hasCurrent || hadBase && !SameEdge(a!, b!);
    }

    private static bool GroupTargetChanged(SceneGraph before, SceneGraph current, string id)
    {
        var hadBase = before.Groups.TryGetValue(id, out var a);
        var hasCurrent = current.Groups.TryGetValue(id, out var b);
        return hadBase != hasCurrent || hadBase && !SameGroup(a!, b!);
    }

    private static bool ElementTargetChanged(SceneGraph before, SceneGraph current, string id) =>
        NodeTargetChanged(before, current, id, null) ||
        EdgeTargetChanged(before, current, id) ||
        GroupTargetChanged(before, current, id);

    private static bool SameNode(SceneNode a, SceneNode b) =>
        a.Kind == b.Kind && a.Label == b.Label && a.Icon == b.Icon &&
        a.Description == b.Description && a.GroupId == b.GroupId &&
        a.Locked == b.Locked && a.LifecycleState == b.LifecycleState;

    private static bool SameEdge(SceneEdge a, SceneEdge b) =>
        a.FromNodeId == b.FromNodeId && a.ToNodeId == b.ToNodeId &&
        a.Kind == b.Kind && a.Label == b.Label && a.Step == b.Step &&
        a.Protocol == b.Protocol && a.Payload == b.Payload &&
        a.DataClassification == b.DataClassification &&
        a.Authentication == b.Authentication &&
        a.InteractionMode == b.InteractionMode &&
        a.LifecycleState == b.LifecycleState;

    private static bool SameGroup(SceneGroup a, SceneGroup b) =>
        a.Label == b.Label && a.ParentGroupId == b.ParentGroupId &&
        a.Subtitle == b.Subtitle && a.BoundaryKind == b.BoundaryKind &&
        a.LifecycleState == b.LifecycleState;

    private static Dictionary<(string Type, string Id), ElementLifecycleState> CaptureLifecycle(
        SceneGraph scene)
    {
        var result = new Dictionary<(string Type, string Id), ElementLifecycleState>();
        foreach (var item in scene.Nodes.Values)
            result[("node", item.Id)] = item.LifecycleState;
        foreach (var item in scene.Edges.Values)
            result[("edge", item.Id)] = item.LifecycleState;
        foreach (var item in scene.Groups.Values)
            result[("group", item.Id)] = item.LifecycleState;
        return result;
    }

    private static IReadOnlyList<ElementLifecycleChange> CompareLifecycle(
        IReadOnlyDictionary<(string Type, string Id), ElementLifecycleState> before,
        IReadOnlyDictionary<(string Type, string Id), ElementLifecycleState> after)
    {
        return before.Keys
            .Union(after.Keys)
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(key =>
            {
                var hadBefore = before.TryGetValue(key, out var previous);
                var hasAfter = after.TryGetValue(key, out var current);
                return new ElementLifecycleChange(
                    key.Type,
                    key.Id,
                    hadBefore ? previous : null,
                    hasAfter ? current : null);
            })
            .Where(change => change.Previous != change.Current)
            .ToArray();
    }

    private static string NormalizeIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && result.Length > 0) result.Append(' ');
                result.Append(char.ToLowerInvariant(c));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }
        return result.ToString();
    }

    private void BeginInFlight(GenerationMode mode)
    {
        lock (_runtimeGate)
        {
            if (mode == GenerationMode.ContinuousExtraction) _fastInFlight++;
            else _deepInFlight++;
        }
    }

    private void EndInFlight(GenerationMode mode, bool succeeded)
    {
        GenerationRuntimeSnapshot snapshot;
        lock (_runtimeGate)
        {
            if (mode == GenerationMode.ContinuousExtraction)
                _fastInFlight = Math.Max(0, _fastInFlight - 1);
            else
                _deepInFlight = Math.Max(0, _deepInFlight - 1);

            var stage = _deepInFlight > 0
                ? GenerationRuntimeStage.DeepSynthesizing
                : _fastInFlight > 0
                    ? GenerationRuntimeStage.Extracting
                    : succeeded
                        ? _runtime.Stage == GenerationRuntimeStage.Degraded
                            ? GenerationRuntimeStage.Degraded
                            : GenerationRuntimeStage.Current
                        : _runtime.Stage == GenerationRuntimeStage.Error
                            ? GenerationRuntimeStage.Error
                            : GenerationRuntimeStage.Idle;
            snapshot = _runtime = _runtime with
            {
                Stage = stage,
                FastInFlight = _fastInFlight,
                DeepInFlight = _deepInFlight,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }
        Notify(RuntimeChanged, snapshot, "runtime_changed");
    }

    private void PublishRuntime(
        GenerationRuntimeStage requestedStage,
        GenerationMode? mode,
        string? safeErrorCode,
        int? baseRevision = null,
        int? appliedRevision = null)
    {
        GenerationRuntimeSnapshot snapshot;
        lock (_runtimeGate)
        {
            var stage = requestedStage == GenerationRuntimeStage.Queued
                ? _deepInFlight > 0
                    ? GenerationRuntimeStage.DeepSynthesizing
                    : _fastInFlight > 0
                        ? GenerationRuntimeStage.Extracting
                        : GenerationRuntimeStage.Queued
                : requestedStage;
            snapshot = _runtime = new GenerationRuntimeSnapshot(
                stage,
                mode,
                _fastInFlight,
                _deepInFlight,
                baseRevision ?? _runtime.BaseRevision,
                appliedRevision ?? Scene.Revision,
                DateTimeOffset.UtcNow,
                safeErrorCode);
        }
        Notify(RuntimeChanged, snapshot, "runtime_changed");
    }

    private static string SafeErrorCode(Exception ex) => ex switch
    {
        TimeoutException => "timeout",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "rate_limited",
        HttpRequestException { StatusCode: { } status } when (int)status >= 500 => "service_failure",
        HttpRequestException => "network",
        _ => "generation_failure",
    };

    private sealed record PatchFilterResult(ScenePatch Patch, int OperationsSkipped);

    private void QueueImageGeneration(GenerateImage op)
    {
        Task task;
        lock (_imageTaskGate)
        {
            task = GenerateImageBoundedAsync(op, _imageCts.Token);
            _imageTasks.Add(task);
        }
        _ = task.ContinueWith(
            completed =>
            {
                lock (_imageTaskGate) _imageTasks.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task GenerateImageBoundedAsync(GenerateImage op, CancellationToken ct)
    {
        await _imageConcurrency.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await GenerateImageAsync(op, ct).ConfigureAwait(false);
        }
        finally
        {
            _imageConcurrency.Release();
        }
    }

    private async Task GenerateImageAsync(GenerateImage op, CancellationToken ct)
    {
        SceneImage? image;
        lock (Scene.SyncRoot)
        {
            if (!Scene.Images.TryGetValue(op.Id, out image)) return;
        }
        if (_imageGenerator is null || !_imageGenerator.IsConfigured)
        {
            UpdateImage(op.Id, live =>
            {
                live.Status = ImageGenerationStatus.Failed;
                live.ErrorMessage = "Image generator not configured";
            });
            return;
        }

        UpdateImage(op.Id, live => live.Status = ImageGenerationStatus.InFlight);

        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _imageGenerator.GenerateAsync(new ImageGenerationRequest(op.Prompt), ct).ConfigureAwait(false);
            sw.Stop();
            UpdateImage(op.Id, live =>
            {
                live.PngBytes = resp.PngBytes;
                live.ModelName = resp.ModelName;
                live.Elapsed = resp.Elapsed;
                live.Status = ImageGenerationStatus.Ready;
            });
            _logger.LogInformation("Image generated id={Id} model={Model} elapsed={Ms}ms",
                op.Id, resp.ModelName, resp.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            sw.Stop();
            UpdateImage(op.Id, live =>
            {
                live.Status = ImageGenerationStatus.Failed;
                live.ErrorMessage = ex.Message;
            });
            _logger.LogWarning(
                "Image generation failed; id={Id} category={Category}",
                op.Id, ex is HttpRequestException ? "image_request_failure" : "image_generation_failure");
        }
    }

    private void UpdateImage(string id, Action<SceneImage> update)
    {
        SceneImage? image;
        lock (Scene.SyncRoot)
        {
            if (!Scene.Images.TryGetValue(id, out image)) return;
            update(image);
            Scene.NotifyImageUpdated(id);
            image = image.Clone();
        }
        Notify(ImageUpdated, new SceneImageUpdated(image), "image_updated");
    }

    public void Clear()
    {
        CancellationTokenSource oldImageCts;
        lock (_imageTaskGate)
        {
            oldImageCts = _imageCts;
            _imageCts = new CancellationTokenSource();
        }
        oldImageCts.Cancel();
        oldImageCts.Dispose();

        var clearPatch = new ScenePatch(new ScenePatchOperation[] { new ClearScene() });
        lock (Scene.SyncRoot)
        {
            _applier.Apply(Scene, clearPatch);
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource imageCts;
        Task[] tasks;
        lock (_imageTaskGate)
        {
            imageCts = _imageCts;
            tasks = _imageTasks.ToArray();
        }
        imageCts.Cancel();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        imageCts.Dispose();
        _fastGate.Dispose();
        _deepGate.Dispose();
        _imageConcurrency.Dispose();
    }

    private void Notify<T>(EventHandler<T>? handler, T value, string eventName)
    {
        try { handler?.Invoke(this, value); }
        catch
        {
            _logger.LogWarning(
                "Diagram event observer failed; event={Event} category={Category}",
                eventName, "event_observer");
        }
    }
}

public enum GenerationRuntimeStage
{
    Idle,
    Queued,
    Extracting,
    DeepSynthesizing,
    Current,
    Behind,
    Degraded,
    Error,
}

public sealed record GenerationRuntimeSnapshot(
    GenerationRuntimeStage Stage,
    GenerationMode? Mode,
    int FastInFlight,
    int DeepInFlight,
    int BaseRevision,
    int AppliedRevision,
    DateTimeOffset UpdatedAt,
    string? SafeErrorCode)
{
    public static GenerationRuntimeSnapshot Idle { get; } = new(
        GenerationRuntimeStage.Idle,
        null,
        0,
        0,
        0,
        0,
        DateTimeOffset.MinValue,
        null);
}

public enum StalePatchDisposition
{
    Fresh,
    AcceptedNoChanges,
    MergedSafely,
    RejectedStale,
    RejectedGenerationEpoch,
    NoSafeApplication,
}

public sealed record DiagramGenerationStarted(
    string GeneratorName,
    string? UserInstruction,
    GenerationMode Mode);
public sealed record DiagramGenerationCompleted(DiagramGenerationResult Result);
public sealed record DiagramGenerationFailed(Exception Error);
public sealed record SceneImageUpdated(SceneImage Image);

public sealed record DiagramGenerationResult(
    ScenePatchResponse Response,
    ScenePatchResult ApplyResult,
    LayoutResult LayoutResult,
    int ImageOpsTriggered,
    SceneBudgetResult? BudgetResult = null,
    int BaseRevision = 0,
    int AppliedRevision = 0,
    IReadOnlyList<ElementLifecycleChange>? LifecycleChanges = null,
    int SkippedOperations = 0,
    StalePatchDisposition StaleDisposition = StalePatchDisposition.Fresh,
    long BaseGenerationEpoch = 0,
    long AppliedGenerationEpoch = 0,
    string? SafeErrorCode = null)
{
    public bool HasSafeApplication =>
        StaleDisposition is StalePatchDisposition.Fresh or
            StalePatchDisposition.AcceptedNoChanges or
            StalePatchDisposition.MergedSafely;
}
