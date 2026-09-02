namespace AudioBoarder.Core.Scene;

public sealed class SceneEdge
{
    public required string Id { get; init; }
    public required string FromNodeId { get; set; }
    public required string ToNodeId { get; set; }
    public EdgeKind Kind { get; set; } = EdgeKind.Flow;
    public string? Label { get; set; }
    public string? Protocol { get; set; }
    public string? Payload { get; set; }
    public string? DataClassification { get; set; }
    public string? Authentication { get; set; }
    public InteractionMode? InteractionMode { get; set; }
    public ElementLifecycleState LifecycleState { get; set; } = ElementLifecycleState.Provisional;

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
        Protocol = Protocol,
        Payload = Payload,
        DataClassification = DataClassification,
        Authentication = Authentication,
        InteractionMode = InteractionMode,
        LifecycleState = LifecycleState,
    };
}
