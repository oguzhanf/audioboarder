using System.ClientModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Azure.AI.OpenAI;
using Azure.Identity;
using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Transcript;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace AudioBoarder.Services.LLM;

/// <summary>
/// Calls Azure OpenAI via the classic <c>/chat/completions</c> endpoint. For
/// reasoning models (gpt-5*, o1, o3) Azure returns HTTP 400 here — the
/// <see cref="SmartScenePatchGenerator"/> automatically falls back to the
/// <see cref="AzureOpenAIResponsesGenerator"/> in that case.
/// </summary>
public sealed class AzureOpenAIScenePatchGenerator : IScenePatchGenerator
{
    private readonly AzureOpenAIOptions _options;
    private readonly ILogger<AzureOpenAIScenePatchGenerator> _logger;

    public AzureOpenAIScenePatchGenerator(
        IOptions<AzureOpenAIOptions> options,
        ILogger<AzureOpenAIScenePatchGenerator>? logger = null)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AzureOpenAIScenePatchGenerator>.Instance;
    }

    public string Name => $"AzureOpenAI/{_options.DeploymentName ?? "?"}";

    public async Task<ScenePatchResponse> GenerateAsync(ScenePatchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_options.IsConfigured)
            throw new InvalidOperationException(
                "AzureOpenAIScenePatchGenerator requires Endpoint and DeploymentName. Run scripts/setup-azure.ps1 to sign in and let Foundry auto-discovery populate these on startup.");

        var sw = Stopwatch.StartNew();
        var prompt = ScenePromptComposer.BuildUserPrompt(request);

        // Continuous-mode requests get the leaner prompt and target the FAST deployment.
        var systemPrompt = ScenePromptComposer.BuildSystemPrompt(_options, request);
        var deploymentName = request.IsContinuous && !string.IsNullOrWhiteSpace(_options.FallbackDeploymentName)
            ? _options.FallbackDeploymentName!
            : _options.DeploymentName!;
        // Options are mutated after interactive sign-in and Foundry discovery.
        // Building from the current snapshot avoids pinning the first endpoint,
        // deployment, API key, or credential for the rest of the process.
        var client = BuildChatClientFor(deploymentName);

        var messages = new ChatMessage[]
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(prompt),
        };

        var attempts = new List<Func<ChatCompletionOptions>>();
        attempts.Add(() => BuildOptions(deploymentName, useSchema: true));
        if (_options.AllowJsonObjectFallback)
        {
            attempts.Add(() => BuildOptions(deploymentName, useSchema: false, useJsonObject: true));
            attempts.Add(() => BuildOptions(deploymentName, useSchema: false, useJsonObject: false));
        }

        Exception? lastError = null;
        foreach (var build in attempts)
        {
            var opts = build();
            var raw = string.Empty;
            try
            {
                var completion = (await client.CompleteChatAsync(messages, opts, ct).ConfigureAwait(false)).Value;
                raw = string.Concat(completion.Content.Select(p => p.Text));
                var jsonOnly = ExtractJson(raw);
                var patch = ScenePatchJson.Deserialize(jsonOnly);
                sw.Stop();
                var label = $"AzureOpenAI/{deploymentName}" + (request.IsContinuous ? " (continuous)" : "");
                return new ScenePatchResponse(patch, label, sw.Elapsed, jsonOnly);
            }
            catch (System.ClientModel.ClientResultException ex) when (ex.Status == 400)
            {
                _logger.LogWarning(
                    "Strict response format rejected; status={Status} category={Category}",
                    ex.Status, "unsupported_response_format");
                lastError = ex;
                continue;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
            {
                // Malformed / unparseable model output. Fall through to the next
                // (looser) response-format strategy rather than discarding the turn.
                _logger.LogWarning(
                    "ScenePatch parse failed; category={Category} responseChars={Chars}; trying next strategy",
                    "invalid_scene_patch", raw.Length);
                lastError = ex;
                continue;
            }
            catch (Exception ex) when (ex is OperationCanceledException)
            {
                // User clicked Stop mid-generation — expected, not an error.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Azure OpenAI completion failed; category={Category}",
                    ex is HttpRequestException ? "model_request_failure" : "model_generation_failure");
                throw;
            }
        }

        throw lastError ?? new InvalidOperationException("All Azure OpenAI completion strategies failed.");
    }

    private ChatCompletionOptions BuildOptions(string deploymentName, bool useSchema, bool useJsonObject = false)
    {
        var opts = new ChatCompletionOptions();
        var isReasoning = IsReasoningModel(deploymentName);
        // gpt-5*/o1*/o3* reasoning deployments reject `temperature` (must be default 1)
        // and reject `max_tokens` (require `max_completion_tokens`). The SDK still
        // emits `max_tokens` in some versions, so the safest cross-version path is
        // to omit both for reasoning models and let the service pick defaults.
        if (!isReasoning && _options.Temperature.HasValue) opts.Temperature = _options.Temperature.Value;
        if (!isReasoning && _options.MaxOutputTokens.HasValue) opts.MaxOutputTokenCount = _options.MaxOutputTokens.Value;
        if (useSchema)
        {
            var schemaBinary = BinaryData.FromString(ScenePatchJsonSchema.Build());
            opts.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "scene_patch",
                jsonSchema: schemaBinary,
                jsonSchemaFormatDescription: "AudioBoarder ScenePatch DSL",
                jsonSchemaIsStrict: true);
        }
        else if (useJsonObject)
        {
            opts.ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat();
        }
        return opts;
    }

    private static bool IsReasoningModel(string? deploymentName)
    {
        if (string.IsNullOrEmpty(deploymentName)) return false;
        var lower = deploymentName.ToLowerInvariant();
        // Heuristic — Azure considers any gpt-5*, o1*, o3* as reasoning models
        // and rejects classic Chat Completions parameters like max_tokens.
        return lower.Contains("gpt-5") || lower.StartsWith("o1") || lower.StartsWith("o3");
    }

    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("LLM returned empty response");
        // Strip markdown fences if the model wraps json in ```json ... ```
        var s = raw.Trim();
        if (s.StartsWith("```"))
        {
            var firstNewline = s.IndexOf('\n');
            if (firstNewline > 0) s = s.Substring(firstNewline + 1);
            var fenceEnd = s.LastIndexOf("```");
            if (fenceEnd > 0) s = s.Substring(0, fenceEnd);
        }
        // If still not pure JSON, try to find the first { and last }
        var firstBrace = s.IndexOf('{');
        var lastBrace = s.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace) return s.Substring(firstBrace, lastBrace - firstBrace + 1);
        return s;
    }

    private ChatClient BuildChatClientFor(string deploymentName)
    {
        var endpoint = new Uri(_options.Endpoint!);
        AzureOpenAIClient client;
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            client = new AzureOpenAIClient(endpoint, new ApiKeyCredential(_options.ApiKey!));
        }
        else if (_options.UseManagedIdentity)
        {
            var credential = _options.Credential;
            if (credential is null)
            {
                var credOptions = new DefaultAzureCredentialOptions
                {
                    TenantId = string.IsNullOrWhiteSpace(_options.TenantId) ? null : _options.TenantId,
                    ExcludeInteractiveBrowserCredential = false,
                    ExcludeAzurePowerShellCredential = true,
                };
                credential = new DefaultAzureCredential(credOptions);
            }
            client = new AzureOpenAIClient(endpoint, credential);
        }
        else
        {
            throw new InvalidOperationException(
                "AzureOpenAI configuration requires ApiKey or UseManagedIdentity = true.");
        }
        return client.GetChatClient(deploymentName);
    }

}
