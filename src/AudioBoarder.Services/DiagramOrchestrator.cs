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
public sealed class DiagramOrchestrator
{
    private readonly IScenePatchGenerator _generator;
    private readonly ScenePatchApplier _applier;
    private readonly ILayoutEngine _layout;
    private readonly TranscriptBuffer _buffer;
    private readonly IImageGenerator? _imageGenerator;
    private readonly ILogger<DiagramOrchestrator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SceneGraph Scene { get; }

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
        ILogger<DiagramOrchestrator>? logger = null)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _imageGenerator = imageGenerator;
        _applier = new ScenePatchApplier();
        _logger = logger ?? NullLogger<DiagramOrchestrator>.Instance;
        Scene = scene ?? new SceneGraph();
    }

    public bool SupportsImages => _imageGenerator is { IsConfigured: true };

    public async Task<DiagramGenerationResult> GenerateAsync(
        string? userInstruction,
        LayoutOptions? layoutOptions = null,
        bool isContinuous = false,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            GenerationStarted?.Invoke(this, new DiagramGenerationStarted(_generator.Name, userInstruction));

            var request = new ScenePatchRequest(
                CurrentScene: Scene.Clone(),
                TranscriptWindow: _buffer.Snapshot(),
                UserInstruction: userInstruction,
                IsContinuous: isContinuous);

            ScenePatchResponse response;
            try
            {
                response = await _generator.GenerateAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // Stop pressed mid-generation — expected, not a failure.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LLM ScenePatch generation failed");
                GenerationFailed?.Invoke(this, new DiagramGenerationFailed(ex));
                throw;
            }

            // Apply the patch and run layout as ONE critical section against the
            // same lock the renderer uses, so the UI thread never paints a
            // half-mutated graph. The applier is best-effort (skips bad ops) so
            // it won't throw on imperfect LLM output.
            ScenePatchResult applyResult;
            LayoutResult layoutResult;
            lock (Scene.SyncRoot)
            {
                applyResult = _applier.Apply(Scene, response.Patch);
                layoutResult = _layout.Apply(Scene, layoutOptions ?? new LayoutOptions());
            }

            var imageOps = response.Patch.Operations.OfType<GenerateImage>().ToList();
            var result = new DiagramGenerationResult(response, applyResult, layoutResult, imageOps.Count);
            GenerationCompleted?.Invoke(this, new DiagramGenerationCompleted(result));

            // Fire image generations in the background — never block the diagram flow on image latency.
            foreach (var op in imageOps)
            {
                _ = Task.Run(() => GenerateImageAsync(op, CancellationToken.None), CancellationToken.None);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task GenerateImageAsync(GenerateImage op, CancellationToken ct)
    {
        if (!Scene.Images.TryGetValue(op.Id, out var image)) return;
        if (_imageGenerator is null || !_imageGenerator.IsConfigured)
        {
            image.Status = ImageGenerationStatus.Failed;
            image.ErrorMessage = "Image generator not configured";
            Scene.NotifyImageUpdated(image.Id);
            ImageUpdated?.Invoke(this, new SceneImageUpdated(image));
            return;
        }

        image.Status = ImageGenerationStatus.InFlight;
        Scene.NotifyImageUpdated(image.Id);
        ImageUpdated?.Invoke(this, new SceneImageUpdated(image));

        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _imageGenerator.GenerateAsync(new ImageGenerationRequest(op.Prompt), ct).ConfigureAwait(false);
            sw.Stop();
            image.PngBytes = resp.PngBytes;
            image.ModelName = resp.ModelName;
            image.Elapsed = resp.Elapsed;
            image.Status = ImageGenerationStatus.Ready;
            _logger.LogInformation("Image generated id={Id} model={Model} elapsed={Ms}ms",
                image.Id, resp.ModelName, resp.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            image.Status = ImageGenerationStatus.Failed;
            image.ErrorMessage = ex.Message;
            _logger.LogWarning(ex, "Image generation failed id={Id}", image.Id);
        }
        finally
        {
            Scene.NotifyImageUpdated(image.Id);
            ImageUpdated?.Invoke(this, new SceneImageUpdated(image));
        }
    }

    public void Clear()
    {
        var clearPatch = new ScenePatch(new ScenePatchOperation[] { new ClearScene() });
        lock (Scene.SyncRoot)
        {
            _applier.Apply(Scene, clearPatch);
        }
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
