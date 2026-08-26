namespace AudioBoarder.Core.Scene;

public sealed class SceneEdge
{
    public required string Id { get; init; }
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public EdgeKind Kind { get; set; } = EdgeKind.Flow;
    public string? Label { get; set; }

    /// <summary>
    /// Position in a numbered walkthrough. Architecture diagrams number the steps of
    /// a request path so a reader can follow it in order; null means the edge is
    /// structural rather than part of a sequence.
    /// </summary>
    public int? Step { get; set; }

    public SceneEdge Clone() => new()
    {
        Id = Id,
        FromNodeId = FromNodeId,
        ToNodeId = ToNodeId,
        Kind = Kind,
        Label = Label,
        Step = Step,
    };
}
