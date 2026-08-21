using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.CognitiveServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioBoarder.Services.LLM;

/// <summary>
/// Discovers Azure OpenAI / MAI deployments in the user's Azure subscription.
/// Uses an externally-supplied credential when available (so the app can
/// share a single signed-in browser credential across services).
/// </summary>
public sealed class FoundryDiscovery
{
    private readonly ILogger<FoundryDiscovery> _logger;
    private TokenCredential? _externalCredential;

    public FoundryDiscovery(ILogger<FoundryDiscovery>? logger = null)
    {
        _logger = logger ?? NullLogger<FoundryDiscovery>.Instance;
    }

    public void SetExternalCredential(TokenCredential credential) => _externalCredential = credential;

    public async Task<DiscoveryResult> DiscoverAsync(
        string? tenantId,
        string? subscriptionId,
        string? preferredDeploymentName = null,
        string? preferredRegion = null,
        TokenCredential? credentialOverride = null,
        CancellationToken ct = default)
    {
        var credential = credentialOverride ?? _externalCredential ?? new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
            ExcludeInteractiveBrowserCredential = false,
            ExcludeAzurePowerShellCredential = true,
        });

        var arm = new ArmClient(credential, defaultSubscriptionId: subscriptionId);

        try
        {
            var sub = string.IsNullOrWhiteSpace(subscriptionId)
                ? (await arm.GetDefaultSubscriptionAsync(ct).ConfigureAwait(false))
                : arm.GetSubscriptionResource(new Azure.Core.ResourceIdentifier($"/subscriptions/{subscriptionId}"));

            var accounts = new List<CognitiveServicesAccountResource>();
            await foreach (var acct in sub.GetCognitiveServicesAccountsAsync(cancellationToken: ct).ConfigureAwait(false))
            {
                var kind = acct.Data.Kind;
                if (string.Equals(kind, "OpenAI", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kind, "AIServices", StringComparison.OrdinalIgnoreCase))
                {
                    accounts.Add(acct);
                }
            }

            if (accounts.Count == 0)
            {
                return new DiscoveryResult(false, null, null, null, null, null, null, null, null, null, null, null,
                    "No OpenAI or AIServices Cognitive Services accounts found in the subscription.");
            }

            // Flatten (account, deployment) pairs across the whole subscription with
            // per-account try/catch so one inaccessible account can't break discovery.
            var all = new List<DeploymentRef>();
            foreach (var account in accounts)
            {
                try
                {
                    await foreach (var dep in account.GetCognitiveServicesAccountDeployments().GetAllAsync(cancellationToken: ct).ConfigureAwait(false))
                    {
                        all.Add(new DeploymentRef(account, dep));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not enumerate deployments for account {Name}; skipping", account.Data.Name);
                }
            }

            if (all.Count == 0)
            {
                var fallback = accounts.First();
                return new DiscoveryResult(false, fallback.Data.Properties.Endpoint?.ToString(),
                    null, null, null, null, null, null, null, null,
                    AccountName: fallback.Data.Name, Region: fallback.Data.Location.Name,
                    Message: $"No deployments found in any account ({accounts.Count} scanned). Create one in Azure AI Foundry.",
                    AccountResourceId: fallback.Id.ToString());
            }

            // CHAT — pick the best across ALL accounts. Honour preferredDeploymentName
            // (exact-match user pin) then highest scoring chat model. Region preference
            // breaks ties between equally-scored chat models.
            var chats = all.Where(r => IsChatModel(r.Model)).ToList();
            var primaryChat = chats
                .OrderByDescending(r => string.Equals(r.Deployment.Data.Name, preferredDeploymentName, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(r => DeploymentScore(r.Model))
                .ThenByDescending(r => RegionMatchScore(r.Account.Data.Location.Name, preferredRegion))
                .FirstOrDefault();

            // FAST chat — prefer SAME account as primary so latency stays low.
            var fastChat = chats
                .Where(r => primaryChat is null || r.Deployment.Data.Name != primaryChat.Deployment.Data.Name)
                .OrderByDescending(r => primaryChat is not null && r.Account.Id == primaryChat.Account.Id)
                .ThenByDescending(r => FastChatScore(r.Model))
                .FirstOrDefault();

            // IMAGE — pick the best across ALL accounts. MAI > gpt-image-*.
            var images = all.Where(r => IsImageModel(r.Model)).ToList();
            var imagePrimary = images
                .OrderByDescending(r => IsMaiModel(r.Model))
                .ThenByDescending(r => ImageScore(r.Model))
                .FirstOrDefault();

            // TRANSCRIBE — pick the best across ALL accounts. MAI > gpt-4o-transcribe.
            var transcribes = all.Where(r => IsTranscribeModel(r.Model)).ToList();
            var transcribePrimary = transcribes
                .OrderByDescending(r => IsMaiModel(r.Model))
                .ThenByDescending(r => TranscribeScore(r.Model))
                .FirstOrDefault();

            string EndpointFor(CognitiveServicesAccountResource acct) =>
                acct.Data.Properties.Endpoint?.ToString()
                ?? $"https://{acct.Data.Name}.cognitiveservices.azure.com/";

            // The "primary" account is the chat-hosting one (used for downstream
            // provisioning and the display label). Falls back to the first account
            // with any deployment if no chat model was found.
            var primaryAccount = primaryChat?.Account ?? all[0].Account;
            var chatEndpoint = primaryChat is not null ? EndpointFor(primaryChat.Account) : EndpointFor(primaryAccount);
            var imageEndpoint = imagePrimary is not null ? EndpointFor(imagePrimary.Account) : null;
            var transcribeEndpoint = transcribePrimary is not null ? EndpointFor(transcribePrimary.Account) : null;

            _logger.LogInformation(
                "Discovered across {AccountCount} account(s); chat={Chat}@{ChatAcct} fast={Fast} image={Image}@{ImageAcct} transcribe={Transcribe}@{TransAcct}",
                accounts.Count,
                primaryChat?.Deployment.Data.Name, primaryChat?.Account.Data.Name,
                fastChat?.Deployment.Data.Name,
                imagePrimary?.Deployment.Data.Name, imagePrimary?.Account.Data.Name,
                transcribePrimary?.Deployment.Data.Name, transcribePrimary?.Account.Data.Name);

            return new DiscoveryResult(
                Success: primaryChat is not null,
                Endpoint: chatEndpoint,
                DeploymentName: primaryChat?.Deployment.Data.Name,
                FallbackDeploymentName: fastChat?.Deployment.Data.Name,
                ImageDeploymentName: imagePrimary?.Deployment.Data.Name,
                ImageDeploymentIsMai: imagePrimary is not null && IsMaiModel(imagePrimary.Model),
                TranscribeDeploymentName: transcribePrimary?.Deployment.Data.Name,
                TranscribeDeploymentIsMai: transcribePrimary is not null && IsMaiModel(transcribePrimary.Model),
                ImageEndpoint: imageEndpoint,
                TranscribeEndpoint: transcribeEndpoint,
                AccountName: primaryAccount.Data.Name,
                Region: primaryAccount.Data.Location.Name,
                Message: primaryChat is null
                    ? "No chat-capable deployment found. AudioBoarder requires a gpt-* or o-series deployment."
                    : $"Using {primaryAccount.Data.Name}/{primaryChat.Deployment.Data.Name}"
                      + (fastChat is not null ? $" (fast: {fastChat.Deployment.Data.Name})" : "")
                      + (imagePrimary is not null ? $" (image: {imagePrimary.Deployment.Data.Name}@{imagePrimary.Account.Data.Name})" : "")
                      + (transcribePrimary is not null ? $" (transcribe: {transcribePrimary.Deployment.Data.Name}@{transcribePrimary.Account.Data.Name})" : ""),
                AccountResourceId: primaryAccount.Id.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Foundry discovery failed");
            return new DiscoveryResult(false, null, null, null, null, null, null, null, null, null, null, null,
                $"Discovery failed: {ex.Message}");
        }
    }

    private static int RegionMatchScore(string? region, string? preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred) || string.IsNullOrWhiteSpace(region)) return 0;
        if (string.Equals(region, preferred, StringComparison.OrdinalIgnoreCase)) return 100;
        // Soft prefix match: "eastus" matches both "eastus" and "eastus2"
        if (region.StartsWith(preferred, StringComparison.OrdinalIgnoreCase) ||
            preferred.StartsWith(region, StringComparison.OrdinalIgnoreCase)) return 50;
        return 0;
    }

    private sealed record DeploymentRef(
        CognitiveServicesAccountResource Account,
        CognitiveServicesAccountDeploymentResource Deployment)
    {
        public string? Model => Deployment.Data.Properties.Model?.Name;
    }

    private static bool IsChatModel(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var lower = name.ToLowerInvariant();
        // Exclude transcription/image/embedding/etc.
        if (lower.Contains("transcribe") || lower.Contains("image") || lower.Contains("embedding") ||
            lower.Contains("voice") || lower.Contains("speech") || lower.Contains("audio") ||
            lower.Contains("dall-e")) return false;
        return lower.Contains("gpt") || lower.StartsWith("o1") || lower.StartsWith("o3") ||
               lower.Contains("deepseek") || lower.Contains("mai-ds") || lower.Contains("mai-1") || lower.Contains("phi");
    }

    private static bool IsImageModel(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var lower = name.ToLowerInvariant();
        return lower.Contains("image") || lower.Contains("dall-e");
    }

    private static bool IsTranscribeModel(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var lower = name.ToLowerInvariant();
        return lower.Contains("transcribe") || lower.Contains("whisper");
    }

    private static bool IsMaiModel(string? name) =>
        !string.IsNullOrEmpty(name) && name.StartsWith("MAI-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ranks a chat model for use as the PRIMARY (deep-analysis) deployment.
    /// Version-aware: gpt-5.6 must beat gpt-5.1, which the old "contains gpt-5 => 100"
    /// rule could not express — every gpt-5.x tied at 100 and the winner was decided
    /// by region tie-break, so a newer frontier deployment on a secondary account lost
    /// to an older one in the preferred region.
    /// </summary>
    internal static int DeploymentScore(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return 0;
        var lower = modelName.ToLowerInvariant();

        var (major, minor) = ParseGptVersion(lower);
        if (major > 0)
        {
            // Family weight dominates, then minor version, then tier.
            var score = 1000 + major * 100 + minor * 10;
            score += TierBonus(lower);
            return score;
        }

        if (lower.StartsWith("o3")) return 1450;
        if (lower.StartsWith("o1")) return 1400;
        if (lower.Contains("deepseek")) return 900;
        if (lower.Contains("mai-ds") || lower.Contains("mai-1")) return 900;
        return 10;
    }

    /// <summary>
    /// Capability tier within the same model version. Microsoft's gpt-5.6 line ships
    /// sol (highest reasoning) > terra > luna (fastest), and "pro" marks the premium
    /// variant of earlier lines.
    /// </summary>
    private static int TierBonus(string lower)
    {
        if (lower.Contains("sol")) return 9;
        if (lower.Contains("pro")) return 8;
        if (lower.Contains("terra")) return 7;
        if (lower.Contains("luna")) return 4;
        if (lower.Contains("chat")) return 2;
        if (lower.Contains("mini") || lower.Contains("nano")) return 0;
        return 5;
    }

    /// <summary>
    /// Extracts a (major, minor) version from a GPT model or deployment name.
    /// Handles both dotted model names ("gpt-5.6-sol") and the dashed deployment
    /// names Azure generates for them ("gpt-5-6-sol"), plus suffixed families
    /// ("gpt-4o" => 4.0). Returns (0, 0) when the name is not a GPT model.
    /// </summary>
    internal static (int Major, int Minor) ParseGptVersion(string lower)
    {
        var m = System.Text.RegularExpressions.Regex.Match(lower, @"gpt-(\d+)[.\-](\d+)");
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, out var maj1) &&
            int.TryParse(m.Groups[2].Value, out var min1))
            return (maj1, min1);

        m = System.Text.RegularExpressions.Regex.Match(lower, @"gpt-(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var maj2))
            return (maj2, 0);

        return (0, 0);
    }

    /// <summary>Score for selecting a FAST chat model for realtime use.</summary>
    private static int FastChatScore(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return 0;
        var lower = modelName.ToLowerInvariant();

        // Prefer the fast sibling of the NEWEST family rather than an old small model:
        // gpt-5.6-luna beats gpt-4o-mini for both quality and structured-output fidelity.
        var (major, minor) = ParseGptVersion(lower);
        if (major >= 5)
        {
            var score = 500 + major * 20 + minor * 5;
            if (lower.Contains("luna") || lower.Contains("mini") || lower.Contains("flash") || lower.Contains("chat"))
                score += 40;           // genuinely fast variants of a modern family
            if (lower.Contains("sol") || lower.Contains("pro"))
                score -= 30;           // reasoning tiers are too slow for continuous mode
            return score;
        }

        if (lower.Contains("mini") || lower.Contains("flash") || lower.Contains("turbo")) return 90;
        if (lower.Contains("gpt-4o")) return 70;
        if (lower.Contains("chat")) return 60;
        if (lower.Contains("gpt-3.5")) return 50;
        if (lower.Contains("pro") || lower.StartsWith("o1") || lower.StartsWith("o3")) return 10;
        return 30;
    }

    private static int ImageScore(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return 0;
        var lower = modelName.ToLowerInvariant();
        if (lower.Contains("mai-image-2.5")) return 100;
        if (lower.Contains("mai-image-2")) return 90;
        if (lower.Contains("gpt-image-2")) return 85;
        if (lower.Contains("gpt-image-1.5")) return 80;
        if (lower.Contains("gpt-image-1")) return 70;
        if (lower.Contains("dall-e-3")) return 50;
        return 10;
    }

    private static int TranscribeScore(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return 0;
        var lower = modelName.ToLowerInvariant();
        if (lower.Contains("mai-transcribe")) return 100;
        if (lower.Contains("gpt-4o-transcribe-diarize")) return 95;
        if (lower.Contains("gpt-4o-transcribe")) return 90;
        if (lower.Contains("gpt-4o-mini-transcribe")) return 85;
        if (lower.Contains("whisper")) return 50;
        return 10;
    }
}

public sealed record DiscoveryResult(
    bool Success,
    string? Endpoint,
    string? DeploymentName,
    string? FallbackDeploymentName,
    string? ImageDeploymentName,
    bool? ImageDeploymentIsMai,
    string? TranscribeDeploymentName,
    bool? TranscribeDeploymentIsMai,
    string? ImageEndpoint,
    string? TranscribeEndpoint,
    string? AccountName,
    string? Region,
    string Message,
    string? AccountResourceId = null);
