namespace AudioBoarder.Core.Scene;

public enum NoteKind
{
    ActionItem,
    Decision,
    Question,
    Risk,
    General,
}

public sealed class SceneNote
{
    public required string Id { get; init; }
    public NoteKind Kind { get; set; } = NoteKind.General;
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset? SourceTimestamp { get; set; }
    public string? Owner { get; set; }

    public SceneNote Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Text = Text,
        SourceTimestamp = SourceTimestamp,
        Owner = Owner,
    };
}
