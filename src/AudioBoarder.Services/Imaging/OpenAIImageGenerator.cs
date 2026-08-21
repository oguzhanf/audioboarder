using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using AudioBoarder.Core.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AudioBoarder.Services.Imaging;

/// <summary>
/// Calls Azure OpenAI image deployments (gpt-image-1, gpt-image-1.5, gpt-image-2)
/// at <c>/openai/deployments/{name}/images/generations</c>.
/// </summary>
public sealed class OpenAIImageGenerator : IImageGenerator
{
    private readonly ImageGeneratorOptions _options;
    private readonly ILogger<OpenAIImageGenerator> _logger;
    private readonly HttpClient _http;
    private readonly TokenCredential? _credential;

    public OpenAIImageGenerator(
        IOptions<ImageGeneratorOptions> options,
        HttpClient? http = null,
        ILogger<OpenAIImageGenerator>? logger = null)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<OpenAIImageGenerator>.Instance;
        _http = http ?? new HttpClient { Timeout = _options.RequestTimeout };
        if (_options.UseManagedIdentity && string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                TenantId = string.IsNullOrWhiteSpace(_options.TenantId) ? null : _options.TenantId,
                ExcludeInteractiveBrowserCredential = false,
                ExcludeAzurePowerShellCredential = true,
            });
        }
    }

    public string Name => $"AzureOpenAI.Image/{_options.DeploymentName ?? "?"}";
    public bool IsConfigured => _options.IsConfigured;

    public async Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsConfigured) throw new InvalidOperationException("OpenAIImageGenerator requires Endpoint + DeploymentName.");

        var sw = Stopwatch.StartNew();
        var endpoint = _options.Endpoint!.TrimEnd('/');
        var url = $"{endpoint}/openai/deployments/{_options.DeploymentName}/images/generations?api-version={_options.OpenAIApiVersion}";

        var payload = new
        {
            prompt = request.Prompt,
            size = $"{request.Width}x{request.Height}",
            n = request.Count,
            output_format = "png",
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        await ApplyAuthAsync(req, ct).ConfigureAwait(false);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI image generation failed status={Status}", resp.StatusCode);
            throw new HttpRequestException($"OpenAI image generation HTTP {(int)resp.StatusCode}: {Truncate(body, 400)}");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            throw new InvalidOperationException("OpenAI image response missing data[]");

        var first = data[0];
        string? revisedPrompt = null;
        if (first.TryGetProperty("revised_prompt", out var rp)) revisedPrompt = rp.GetString();

        if (!first.TryGetProperty("b64_json", out var b64))
            throw new InvalidOperationException("OpenAI image response missing b64_json");

        var png = Convert.FromBase64String(b64.GetString()!);
        sw.Stop();
        return new ImageGenerationResponse(png, Name, sw.Elapsed, revisedPrompt);
    }

    private async Task ApplyAuthAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            req.Headers.Add("api-key", _options.ApiKey);
            return;
        }
        if (_credential is null) throw new InvalidOperationException("No credential available for image generation.");
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }), ct).ConfigureAwait(false);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
