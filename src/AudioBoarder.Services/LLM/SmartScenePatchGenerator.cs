using AudioBoarder.Core.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AudioBoarder.Services.LLM;

/// <summary>
/// Routes a ScenePatch request to the correct backing client based on the
/// configured deployment name: reasoning-family models (gpt-5*, o1*, o3*) use
/// the new Responses API; classic chat models use Chat Completions.
/// Falls back automatically if one path returns 400 with "operation unsupported".
/// </summary>
public sealed class SmartScenePatchGenerator : IScenePatchGenerator
{
    private readonly AzureOpenAIScenePatchGenerator _chat;
    private readonly AzureOpenAIResponsesGenerator _responses;
    private readonly AzureOpenAIOptions _options;
    private readonly ILogger<SmartScenePatchGenerator> _logger;
    private IScenePatchGenerator? _preferred;

    public SmartScenePatchGenerator(
        IOptions<AzureOpenAIOptions> options,
        AzureOpenAIScenePatchGenerator chat,
        AzureOpenAIResponsesGenerator responses,
        ILogger<SmartScenePatchGenerator> logger)
    {
        _options = options.Value;
        _chat = chat;
        _responses = responses;
        _logger = logger;
    }

    public string Name => _preferred?.Name ?? $"Smart({_options.DeploymentName})";

    public async Task<ScenePatchResponse> GenerateAsync(ScenePatchRequest request, CancellationToken ct)
    {
        // Continuous mode prefers the fast deployment (e.g. gpt-5.3-chat) over the
        // primary pro/reasoning model so realtime updates stay snappy.
        var deploymentName = request.IsContinuous && !string.IsNullOrWhiteSpace(_options.FallbackDeploymentName)
            ? _options.FallbackDeploymentName
            : _options.DeploymentName;

        var first = PickByModelName(deploymentName);
        var second = first == _chat ? (IScenePatchGenerator)_responses : _chat;
        try
        {
            var resp = await first.GenerateAsync(request, ct).ConfigureAwait(false);
            if (!request.IsContinuous) _preferred = first;
            return resp;
        }
        catch (Exception ex) when (IsUnsupportedOperation(ex))
        {
            _logger.LogInformation("First generator {First} unsupported, trying {Second}", first.Name, second.Name);
            var resp = await second.GenerateAsync(request, ct).ConfigureAwait(false);
            if (!request.IsContinuous) _preferred = second;
            return resp;
        }
    }

    private IScenePatchGenerator PickByModelName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return _chat;
        var lower = name.ToLowerInvariant();
        if (lower.Contains("gpt-5") || lower.StartsWith("o1") || lower.StartsWith("o3") || lower.Contains("reasoning"))
            return _responses;
        return _chat;
    }

    private static bool IsUnsupportedOperation(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("400") || msg.Contains("unsupported", StringComparison.OrdinalIgnoreCase);
    }
}
