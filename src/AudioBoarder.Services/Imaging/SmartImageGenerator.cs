using AudioBoarder.Core.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AudioBoarder.Services.Imaging;

/// <summary>
/// Routes image-generation requests to MAI-Image when the configured deployment
/// is a MAI model, otherwise to OpenAI gpt-image-*. Falls back across providers
/// if the primary returns a non-retriable error.
/// </summary>
public sealed class SmartImageGenerator : IImageGenerator
{
    private readonly MaiImageGenerator _mai;
    private readonly OpenAIImageGenerator _openai;
    private readonly ImageGeneratorOptions _options;
    private readonly ILogger<SmartImageGenerator> _logger;
    private IImageGenerator? _preferred;

    public SmartImageGenerator(
        IOptions<ImageGeneratorOptions> options,
        MaiImageGenerator mai,
        OpenAIImageGenerator openai,
        ILogger<SmartImageGenerator> logger)
    {
        _options = options.Value;
        _mai = mai;
        _openai = openai;
        _logger = logger;
    }

    public string Name => _preferred?.Name ?? $"SmartImage({_options.DeploymentName})";
    public bool IsConfigured => _options.IsConfigured;

    public async Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request, CancellationToken ct)
    {
        var first = _preferred ?? PickByModelName(_options.DeploymentName);
        var second = first == _mai ? (IImageGenerator)_openai : _mai;
        try
        {
            var resp = await first.GenerateAsync(request, ct).ConfigureAwait(false);
            _preferred = first;
            return resp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image generator {First} failed; trying {Second}", first.Name, second.Name);
            var resp = await second.GenerateAsync(request, ct).ConfigureAwait(false);
            _preferred = second;
            return resp;
        }
    }

    private IImageGenerator PickByModelName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return _openai;
        return name.StartsWith("MAI-", StringComparison.OrdinalIgnoreCase) ? _mai : _openai;
    }
}
