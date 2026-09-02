using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;

namespace AudioBoarder.Core.LLM;

public enum GenerationMode
{
    ContinuousExtraction,
    DeepSynthesis,
    ManualRefine,
}

public sealed record ScenePatchRequest(
    SceneGraph CurrentScene,
    IReadOnlyList<TranscriptSegment> TranscriptWindow,
    string? UserInstruction = null,
    int MaxNodes = 60,
    GenerationMode Mode = GenerationMode.DeepSynthesis,
    DiagramIntent DiagramIntent = DiagramIntent.SoftwareSystemArchitecture,
    DiagramIntentState? IntentState = null,
    long GenerationEpoch = 0)
{
    public bool IsContinuous => Mode == GenerationMode.ContinuousExtraction;
}

public sealed record ScenePatchResponse(
    ScenePatch Patch,
    string ModelName,
    TimeSpan Elapsed,
    string? RawJson = null);

/// <summary>
/// Given the current scene and a transcript window, produce a ScenePatch.
/// Production implementation is the Azure OpenAI Smart router; tests use
/// in-memory doubles.
/// </summary>
public interface IScenePatchGenerator
{
    string Name { get; }
    Task<ScenePatchResponse> GenerateAsync(ScenePatchRequest request, CancellationToken ct);
}
