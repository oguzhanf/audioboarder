namespace AudioBoarder.Core.Patch;

/// <summary>
/// A batch of operations the LLM emits in a single response. Operations
/// are validated and applied in order to a working copy; invalid operations
/// are skipped and the resulting graph is committed atomically.
/// </summary>
public sealed record ScenePatch(IReadOnlyList<ScenePatchOperation> Operations)
{
    public static ScenePatch Empty { get; } = new(Array.Empty<ScenePatchOperation>());
}
