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
/// Calls the Microsoft MAI image generation endpoint at
/// <c>{services-endpoint}/mai/v1/images/generations</c>. Requires a Foundry
/// resource in a MAI-supported region (East US, West US, West Central US,
/// West Europe, Sweden Central, South India, UAE North).
/// </summary>
public sealed class MaiImageGenerator : IImageGenerator
{
    private readonly ImageGeneratorOptions _options;
    private readonly ILogger<MaiImageGenerator> _logger;
    private readonly HttpClient _http;
    private readonly TokenCredential? _credential;

    public MaiImageGenerator(
        IOptions<ImageGeneratorOptions> options,
        HttpClient? http = null,
        ILogger<MaiImageGenerator>? logger = null)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<MaiImageGenerator>.Instance;
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

    public string Name => $"MAI.Image/{_options.DeploymentName ?? "?"}";
    public bool IsConfigured => _options.IsConfigured;

    public async Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsConfigured) throw new InvalidOperationException("MaiImageGenerator requires Endpoint + DeploymentName.");

        var sw = Stopwatch.StartNew();
        // MAI uses the services.ai.azure.com host pattern. Accept either form and normalise.
        var endpoint = _options.Endpoint!.TrimEnd('/');
        if (endpoint.Contains(".cognitiveservices.azure.com"))
            endpoint = endpoint.Replace(".cognitiveservices.azure.com", ".services.ai.azure.com");
        var url = $"{endpoint}/mai/v1/images/generations";

        var payload = new
        {
            model = _options.DeploymentName,
            prompt = request.Prompt,
            width = request.Width,
            height = request.Height,
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        await ApplyAuthAsync(req, ct).ConfigureAwait(false);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("MAI image generation failed status={Status}", resp.StatusCode);
            throw new HttpRequestException($"MAI image generation HTTP {(int)resp.StatusCode}: {Truncate(body, 400)}");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            throw new InvalidOperationException("MAI image response missing data[]");

        var first = data[0];
        if (!first.TryGetProperty("b64_json", out var b64))
            throw new InvalidOperationException("MAI image response missing b64_json");

        var png = Convert.FromBase64String(b64.GetString()!);
        sw.Stop();
        return new ImageGenerationResponse(png, Name, sw.Elapsed);
    }

    private async Task ApplyAuthAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            req.Headers.Add("api-key", _options.ApiKey);
            return;
        }
        var credential = _options.Credential ?? _credential;
        if (credential is null) throw new InvalidOperationException("No credential available for image generation.");
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }), ct).ConfigureAwait(false);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
