using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;

namespace AudioBoarder.Core.LLM;

public sealed record ScenePatchRequest(
    SceneGraph CurrentScene,
    IReadOnlyList<TranscriptSegment> TranscriptWindow,
    string? UserInstruction = null,
    int MaxNodes = 15,
    /// <summary>
    /// True when this call is part of the continuous mid-meeting summarizer.
    /// Generators should prefer a fast deployment, use the continuous system
    /// prompt, and never wipe the scene.
    /// </summary>
    bool IsContinuous = false);

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
