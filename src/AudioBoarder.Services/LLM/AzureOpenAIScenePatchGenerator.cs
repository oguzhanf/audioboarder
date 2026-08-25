using System.ClientModel;
using System.Diagnostics;
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
    private readonly Lazy<ChatClient> _chatClient;

    public AzureOpenAIScenePatchGenerator(
        IOptions<AzureOpenAIOptions> options,
        ILogger<AzureOpenAIScenePatchGenerator>? logger = null)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AzureOpenAIScenePatchGenerator>.Instance;
        _chatClient = new Lazy<ChatClient>(BuildChatClient);
    }

    public string Name => $"AzureOpenAI/{_options.DeploymentName ?? "?"}";

    public async Task<ScenePatchResponse> GenerateAsync(ScenePatchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_options.IsConfigured)
            throw new InvalidOperationException(
                "AzureOpenAIScenePatchGenerator requires Endpoint and DeploymentName. Run scripts/setup-azure.ps1 to sign in and let Foundry auto-discovery populate these on startup.");

        var sw = Stopwatch.StartNew();
        var prompt = BuildUserPrompt(request);

        // Continuous-mode requests get the leaner prompt and target the FAST deployment.
        var systemPrompt = request.IsContinuous ? _options.ContinuousSystemPrompt : _options.SystemPrompt;
        var deploymentName = request.IsContinuous && !string.IsNullOrWhiteSpace(_options.FallbackDeploymentName)
            ? _options.FallbackDeploymentName!
            : _options.DeploymentName!;
        var client = deploymentName == _options.DeploymentName
            ? _chatClient.Value
            : BuildChatClientFor(deploymentName);

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
                _logger.LogWarning("Strict mode rejected by deployment; retrying with looser response format. {Msg}", ex.Message);
                lastError = ex;
                continue;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
            {
                // Malformed / unparseable model output. Fall through to the next
                // (looser) response-format strategy rather than discarding the
                // whole turn, and log the payload so it can be diagnosed.
                _logger.LogWarning(ex, "ScenePatch parse failed; trying next strategy. Raw: {Raw}",
                    raw.Length > 400 ? raw[..400] + "…" : raw);
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
                _logger.LogError(ex, "Azure OpenAI completion failed");
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

    private ChatClient BuildChatClient() => BuildChatClientFor(_options.DeploymentName!);

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
            var credOptions = new DefaultAzureCredentialOptions
            {
                TenantId = string.IsNullOrWhiteSpace(_options.TenantId) ? null : _options.TenantId,
                ExcludeInteractiveBrowserCredential = false,
                ExcludeAzurePowerShellCredential = true,
            };
            client = new AzureOpenAIClient(endpoint, new DefaultAzureCredential(credOptions));
        }
        else
        {
            throw new InvalidOperationException(
                "AzureOpenAI configuration requires ApiKey or UseManagedIdentity = true.");
        }
        return client.GetChatClient(deploymentName);
    }

    private static string BuildUserPrompt(ScenePatchRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Current scene (JSON)");
        sb.AppendLine(SceneSummariser.Summarise(request.CurrentScene));
        sb.AppendLine();
        sb.AppendLine("## Transcript (last N segments)");
        foreach (var seg in request.TranscriptWindow)
            sb.AppendLine($"- [{seg.Speaker}] {seg.Start:HH:mm:ss}: {seg.Text}");
        if (!string.IsNullOrWhiteSpace(request.UserInstruction))
        {
            sb.AppendLine();
            sb.AppendLine("## User instruction");
            sb.AppendLine(request.UserInstruction);
        }
        sb.AppendLine();
        sb.AppendLine($"Respond with a ScenePatch JSON. Max {request.MaxNodes} nodes total.");
        return sb.ToString();
    }
}

internal static class SceneSummariser
{
    public static string Summarise(Core.Scene.SceneGraph graph)
    {
        var sb = new StringBuilder();
        sb.Append("nodes=").Append(graph.Nodes.Count)
          .Append(" edges=").Append(graph.Edges.Count)
          .Append(" groups=").Append(graph.Groups.Count)
          .Append(" notes=").Append(graph.Notes.Count).AppendLine();
        foreach (var n in graph.Nodes.Values)
        {
            // Surface icon/description/group so a refine pass can see what is already
            // enriched and extend it instead of silently stripping it back to a bare box.
            sb.Append($"  N {n.Id} ({n.Kind}) {n.Label}");
            if (!string.IsNullOrWhiteSpace(n.Description)) sb.Append($" desc=\"{n.Description}\"");
            if (!string.IsNullOrWhiteSpace(n.GroupId)) sb.Append($" group={n.GroupId}");
            sb.AppendLine();
        }
        foreach (var e in graph.Edges.Values)
            sb.AppendLine($"  E {e.Id}: {e.FromNodeId} -> {e.ToNodeId} {e.Kind} {e.Label}");
        foreach (var g in graph.Groups.Values)
            sb.AppendLine($"  G {g.Id}: {g.Label}");
        return sb.ToString();
    }
}
