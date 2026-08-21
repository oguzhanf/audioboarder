using System.Text.Json.Serialization;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Core.Patch;

/// <summary>
/// Polymorphic union of operations the LLM can emit inside a <see cref="ScenePatch"/>.
/// Discriminated by the <c>op</c> property in JSON.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(ClearScene), "clear_scene")]
[JsonDerivedType(typeof(AddNode), "add_node")]
[JsonDerivedType(typeof(UpdateNode), "update_node")]
[JsonDerivedType(typeof(DeleteNode), "delete_node")]
[JsonDerivedType(typeof(Connect), "connect")]
[JsonDerivedType(typeof(Disconnect), "disconnect")]
[JsonDerivedType(typeof(Relabel), "relabel")]
[JsonDerivedType(typeof(GroupOp), "group")]
[JsonDerivedType(typeof(UngroupOp), "ungroup")]
[JsonDerivedType(typeof(NoteUpsert), "note_upsert")]
[JsonDerivedType(typeof(NoteDelete), "note_delete")]
[JsonDerivedType(typeof(GenerateImage), "generate_image")]
[JsonDerivedType(typeof(DeleteImage), "delete_image")]
public abstract record ScenePatchOperation;

public sealed record ClearScene() : ScenePatchOperation;

public sealed record AddNode(
    string Id,
    NodeKind Kind,
    string Label,
    string? GroupId = null,
    PositionHint? Position = null,
    string? Icon = null,
    string? Description = null) : ScenePatchOperation;

public sealed record UpdateNode(
    string Id,
    NodeKind? Kind = null,
    string? Label = null,
    string? GroupId = null,
    PositionHint? Position = null,
    string? Icon = null,
    string? Description = null) : ScenePatchOperation;

public sealed record DeleteNode(string Id) : ScenePatchOperation;

public sealed record Connect(
    string Id,
    string From,
    string To,
    EdgeKind Kind = EdgeKind.Flow,
    string? Label = null) : ScenePatchOperation;

public sealed record Disconnect(string Id) : ScenePatchOperation;

public sealed record Relabel(string Id, string Label) : ScenePatchOperation;

public sealed record GroupOp(string Id, string Label, IReadOnlyList<string> NodeIds) : ScenePatchOperation;

public sealed record UngroupOp(string Id) : ScenePatchOperation;

public sealed record NoteUpsert(
    string Id,
    NoteKind Kind,
    string Text,
    string? Owner = null,
    DateTimeOffset? SourceTimestamp = null) : ScenePatchOperation;

public sealed record NoteDelete(string Id) : ScenePatchOperation;

/// <summary>
/// Asks the host to generate an illustrative image and attach it to the scene.
/// The LLM emits this when a visual would meaningfully complement the diagram
/// (e.g. concept art for a discussed product, a mood-board for a UX flow).
/// The patch applier records a pending <see cref="Core.Imaging.SceneImage"/>;
/// the orchestrator fires the actual image-generation request asynchronously.
/// </summary>
public sealed record GenerateImage(
    string Id,
    string Prompt,
    string? AttachToNodeId = null) : ScenePatchOperation;

public sealed record DeleteImage(string Id) : ScenePatchOperation;

/// <summary>
/// Logical placement hint emitted by the LLM. The renderer / layout engine
/// converts hints into pixel coordinates; the LLM never emits raw pixels.
/// </summary>
public sealed record PositionHint(
    PositionHintKind Kind,
    string? Reference = null);

public enum PositionHintKind
{
    Auto,
    Above,
    Below,
    LeftOf,
    RightOf,
    Near,
    InsideGroup,
}
