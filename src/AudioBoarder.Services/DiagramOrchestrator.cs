using System.Diagnostics;
using AudioBoarder.Core.Imaging;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
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
    private int _restoredNodeFloor;
    private int _restoredNoteFloor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _imageConcurrency = new(2, 2);
    private readonly object _imageTaskGate = new();
    private readonly HashSet<Task> _imageTasks = new();
    private CancellationTokenSource _imageCts = new();

    public SceneGraph Scene { get; }

    /// <summary>
    /// How much recent transcript a continuous pass sees. Long enough to carry a
    /// complete thought, short enough that the prompt does not grow with the meeting.
    /// </summary>
    public static readonly TimeSpan ContinuousTranscriptWindow = TimeSpan.FromSeconds(75);

    public event EventHandler<DiagramGenerationStarted>? GenerationStarted;
    public event EventHandler<DiagramGenerationCompleted>? GenerationCompleted;
    public event EventHandler<DiagramGenerationFailed>? GenerationFailed;
    public event EventHandler<SceneImageUpdated>? ImageUpdated;

    public DiagramOrchestrator(
        IScenePatchGenerator generator,
        ILayoutEngine layout,
        TranscriptBuffer buffer,
        SceneGraph? scene = null,
        IImageGenerator? imageGenerator = null,
        ILogger<DiagramOrchestrator>? logger = null,
        SceneBudget? budget = null)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _imageGenerator = imageGenerator;
        _applier = new ScenePatchApplier();
        _logger = logger ?? NullLogger<DiagramOrchestrator>.Instance;
        _budget = budget ?? SceneBudget.Default;
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
            Math.Max(_budget.MaxNodes, _restoredNodeFloor),
            Math.Max(_budget.MaxNotes, _restoredNoteFloor));

    /// <summary>
    /// Re-sizes every node to its text and re-runs layout without calling the model.
    /// Used after restoring a session, whose persisted geometry may predate the
    /// current sizing and layout rules.
    /// </summary>
    public void Relayout(LayoutOptions? layoutOptions = null)
    {
        lock (Scene.SyncRoot)
        {
            NodeSizer.ApplyTo(Scene);
            _layout.Apply(Scene, layoutOptions ?? new LayoutOptions());
        }
    }

    public async Task<DiagramGenerationResult> GenerateAsync(
        string? userInstruction,
        LayoutOptions? layoutOptions = null,
        bool isContinuous = false,
        bool isAutomatic = false,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            GenerationStarted?.Invoke(this, new DiagramGenerationStarted(_generator.Name, userInstruction));

            var request = new ScenePatchRequest(
                CurrentScene: Scene.Clone(),
                // Continuous passes get only what was just said. The scene already
                // encodes everything earlier, so re-sending the whole rolling window
                // every few seconds only inflates the prompt (and the latency) as the
                // meeting goes on. Deep passes still see the full window.
                TranscriptWindow: isContinuous
                    ? _buffer.SnapshotRecent(ContinuousTranscriptWindow)
                    : _buffer.Snapshot(),
                UserInstruction: userInstruction,
                IsContinuous: isContinuous);

            var response = await _generator.GenerateAsync(request, ct).ConfigureAwait(false);

            // Apply the patch and run layout as ONE critical section against the
            // same lock the renderer uses, so the UI thread never paints a
            // half-mutated graph. The applier is best-effort (skips bad ops) so
            // it won't throw on imperfect LLM output.
            ScenePatchResult applyResult;
            LayoutResult layoutResult;
            SceneBudgetResult budgetResult;
            lock (Scene.SyncRoot)
            {
                // Filter inside the same lock as the apply: reading the locked-node
                // set outside it leaves a window where a drag completing mid-flight
                // would not be seen, and the node the user just pinned gets deleted.
                var safePatch = FilterGeneratedPatch(
                    response.Patch, Scene, isContinuous, isAutomatic);
                response = response with { Patch = safePatch };

                applyResult = _applier.Apply(Scene, safePatch);
                // Trim before layout so positions are only computed for what survives.
                budgetResult = SceneBudgetEnforcer.Enforce(Scene, EffectiveBudget());
                // Size every box to its own text BEFORE layout, so the footprint the
                // layout engine reserves matches what actually gets drawn.
                NodeSizer.ApplyTo(Scene);
                layoutResult = _layout.Apply(Scene, layoutOptions ?? new LayoutOptions());
            }

            if (budgetResult.ChangedAnything)
            {
                _logger.LogInformation(
                    "Scene budget trimmed the board: {Nodes} node(s), {Notes} note(s), {Groups} empty group(s)",
                    budgetResult.NodesEvicted, budgetResult.NotesEvicted, budgetResult.GroupsRemoved);
            }

            var imageOps = response.Patch.Operations.OfType<GenerateImage>().ToList();
            var result = new DiagramGenerationResult(response, applyResult, layoutResult, imageOps.Count);
            GenerationCompleted?.Invoke(this, new DiagramGenerationCompleted(result));

            // Fire image generations in the background — never block the diagram flow on image latency.
            foreach (var op in imageOps)
                QueueImageGeneration(op);
            return result;
        }
        catch (OperationCanceledException ex)
        {
            GenerationFailed?.Invoke(this, new DiagramGenerationFailed(ex));
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagram generation failed");
            GenerationFailed?.Invoke(this, new DiagramGenerationFailed(ex));
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static ScenePatch FilterGeneratedPatch(
        ScenePatch patch, SceneGraph scene, bool isContinuous, bool isAutomatic)
    {
        // Caller holds Scene.SyncRoot.
        var lockedNodeIds = scene.Nodes.Values
            .Where(n => n.Locked)
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

        var safe = patch.Operations
            .Where(op => IsAllowed(op, lockedNodeIds, isContinuous, isAutomatic))
            .ToArray();
        return safe.Length == patch.Operations.Count ? patch : new ScenePatch(safe);
    }

    private static bool IsAllowed(
        ScenePatchOperation op, HashSet<string> lockedNodeIds, bool isContinuous, bool isAutomatic)
    {
        // Never honour a wholesale wipe from the model; Clear is a user action.
        if (op is ClearScene) return false;

        // Protect user-pinned nodes from deletion on any pass.
        if (op is DeleteNode delete && lockedNodeIds.Contains(delete.Id)) return false;

        // Quick continuous passes are purely additive: they run every few seconds, so
        // a single mis-fire must never be able to remove anything.
        if (isContinuous &&
            op is DeleteNode or Disconnect or UngroupOp or NoteDelete or DeleteImage)
        {
            return false;
        }

        // The periodic deep pass MAY prune structure — without that the board could
        // only ever grow. But notes are the meeting's actual deliverable (decisions,
        // action items, risks), they cannot be pinned, and autosave overwrites the
        // only copy within milliseconds. So an unattended pass never deletes a note
        // or a generated image; only a user-invoked Deep Refine can.
        if (isAutomatic && op is NoteDelete or DeleteImage) return false;

        return true;
    }

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
            _logger.LogWarning(ex, "Image generation failed id={Id}", op.Id);
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
        ImageUpdated?.Invoke(this, new SceneImageUpdated(image));
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
        _gate.Dispose();
        _imageConcurrency.Dispose();
    }
}

public sealed record DiagramGenerationStarted(string GeneratorName, string? UserInstruction);
public sealed record DiagramGenerationCompleted(DiagramGenerationResult Result);
public sealed record DiagramGenerationFailed(Exception Error);
public sealed record SceneImageUpdated(SceneImage Image);

public sealed record DiagramGenerationResult(
    ScenePatchResponse Response,
    ScenePatchResult ApplyResult,
    LayoutResult LayoutResult,
    int ImageOpsTriggered);
