namespace AudioBoarder.Core.Scene;

public sealed class SceneEdge
{
    public required string Id { get; init; }
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public EdgeKind Kind { get; set; } = EdgeKind.Flow;
    public string? Label { get; set; }

    public SceneEdge Clone() => new()
    {
        Id = Id,
        FromNodeId = FromNodeId,
        ToNodeId = ToNodeId,
        Kind = Kind,
        Label = Label,
    };
}
