namespace AudioBoarder.Core.Scene;

public sealed class SceneGroup
{
    public required string Id { get; init; }
    public string Label { get; set; } = string.Empty;

    public SceneGroup Clone() => new()
    {
        Id = Id,
        Label = Label,
    };
}
