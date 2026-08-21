namespace AudioBoarder.Core.Imaging;

/// <summary>
/// A generated image attached to the scene. Lives alongside nodes/edges in
/// the SceneGraph and renders as a thumbnail near its anchor node when supplied.
/// </summary>
public sealed class SceneImage
{
    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public string? AttachedToNodeId { get; set; }
    public byte[]? PngBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ImageGenerationStatus Status { get; set; } = ImageGenerationStatus.Pending;
    public string? ErrorMessage { get; set; }
    public string? ModelName { get; set; }
    public TimeSpan? Elapsed { get; set; }

    public SceneImage Clone() => new()
    {
        Id = Id,
        Prompt = Prompt,
        AttachedToNodeId = AttachedToNodeId,
        PngBytes = PngBytes,
        CreatedAt = CreatedAt,
        Status = Status,
        ErrorMessage = ErrorMessage,
        ModelName = ModelName,
        Elapsed = Elapsed,
    };
}

public enum ImageGenerationStatus
{
    Pending,
    InFlight,
    Ready,
    Failed,
}
