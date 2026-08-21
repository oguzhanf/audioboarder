namespace AudioBoarder.Core.Imaging;

public sealed record ImageGenerationRequest(
    string Prompt,
    int Width = 1024,
    int Height = 1024,
    int Count = 1,
    string? Style = null);

public sealed record ImageGenerationResponse(
    byte[] PngBytes,
    string ModelName,
    TimeSpan Elapsed,
    string? RevisedPrompt = null);

/// <summary>
/// Cloud image generator. Implementations include the OpenAI gpt-image-*
/// family and the MAI-Image-* family; SmartImageGenerator auto-selects
/// based on which deployment is present in the user's Foundry resource.
/// </summary>
public interface IImageGenerator
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request, CancellationToken ct);
}
