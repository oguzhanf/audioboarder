namespace AudioBoarder.Core.Patch;

/// <summary>
/// A batch of operations the LLM emits in a single response. Operations
/// are applied in order; one invalid operation aborts the whole batch
/// (transactional semantics — see <see cref="ScenePatchApplier"/>).
/// </summary>
public sealed record ScenePatch(IReadOnlyList<ScenePatchOperation> Operations)
{
    public static ScenePatch Empty { get; } = new(Array.Empty<ScenePatchOperation>());
}
