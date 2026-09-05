using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.CognitiveServices;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioBoarder.Services.LLM;

/// <summary>
/// Discovers Azure OpenAI / MAI deployments in the user's Azure subscription.
/// Uses an externally-supplied credential when available (so the app can
/// share a single signed-in browser credential across services).
/// </summary>
public interface IFoundryDiscovery
{
    Task<DiscoveryResult> DiscoverAsync(
        string? tenantId,
        string? subscriptionId,
        string? preferredDeploymentName = null,
        string? preferredRegion = null,
        string? preferredImageDeploymentName = null,
        string? preferredTranscribeDeploymentName = null,
        TokenCredential? credentialOverride = null,
        CancellationToken ct = default);
}

public sealed class FoundryDiscovery : IFoundryDiscovery
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
        string? preferredImageDeploymentName = null,
        string? preferredTranscribeDeploymentName = null,
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
            var enumerationFailure = DiscoveryFailureKind.None;
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
                    enumerationFailure = MergeFailure(enumerationFailure, ClassifyFailure(ex));
                    _logger.LogWarning("Could not enumerate deployments for one account; category={Category}",
                        ex.GetType().Name);
                }
            }

            if (all.Count == 0)
            {
                var fallback = accounts.First();
                if (enumerationFailure != DiscoveryFailureKind.None)
                {
                    return new DiscoveryResult(false, fallback.Data.Properties.Endpoint?.ToString(),
                        null, null, null, null, null, null, null, null,
                        AccountName: fallback.Data.Name, Region: fallback.Data.Location.Name,
                        Message: enumerationFailure == DiscoveryFailureKind.AccessDenied
                            ? "Signed in, but Azure denied permission to enumerate deployments."
                            : "Azure deployment enumeration could not be completed.",
                        AccountResourceId: fallback.Id.ToString(),
                        FailureKind: enumerationFailure);
                }
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
            var imagePrimary = string.IsNullOrWhiteSpace(preferredImageDeploymentName)
                ? images
                    .OrderByDescending(r => IsMaiModel(r.Model))
                    .ThenByDescending(r => ImageScore(r.Model))
                    .FirstOrDefault()
                : images.FirstOrDefault(r => string.Equals(
                    r.Deployment.Data.Name,
                    preferredImageDeploymentName,
                    StringComparison.OrdinalIgnoreCase));

            // TRANSCRIBE — pick the best across ALL accounts. MAI > gpt-4o-transcribe.
            var transcribes = all.Where(r => IsTranscribeModel(r.Model)).ToList();
            var transcribePrimary =
                string.IsNullOrWhiteSpace(preferredTranscribeDeploymentName)
                    ? transcribes
                        .OrderByDescending(r => IsMaiModel(r.Model))
                        .ThenByDescending(r => TranscribeScore(r.Model))
                        .FirstOrDefault()
                    : transcribes.FirstOrDefault(r => string.Equals(
                        r.Deployment.Data.Name,
                        preferredTranscribeDeploymentName,
                        StringComparison.OrdinalIgnoreCase));

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
                "Discovered across {AccountCount} account(s); chat={Chat} fast={Fast} image={Image} transcribe={Transcribe}",
                accounts.Count,
                primaryChat?.Deployment.Data.Name,
                fastChat?.Deployment.Data.Name,
                imagePrimary?.Deployment.Data.Name,
                transcribePrimary?.Deployment.Data.Name);

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
                $"Discovery failed: {ex.Message}", FailureKind: ClassifyFailure(ex));
        }
    }

    private static DiscoveryFailureKind MergeFailure(
        DiscoveryFailureKind current,
        DiscoveryFailureKind next)
    {
        if (current == DiscoveryFailureKind.AccessDenied || next == DiscoveryFailureKind.AccessDenied)
            return DiscoveryFailureKind.AccessDenied;
        return current == DiscoveryFailureKind.None ? next : current;
    }

    internal static DiscoveryFailureKind ClassifyFailure(Exception ex)
    {
        if (ex is AuthenticationFailedException { InnerException: { } innerException })
        {
            var innerKind = ClassifyFailure(innerException);
            if (innerKind != DiscoveryFailureKind.Unknown)
                return innerKind;
        }
        if (ex is AuthenticationRequiredException or CredentialUnavailableException or AuthenticationFailedException)
            return DiscoveryFailureKind.Authentication;
        if (ex is RequestFailedException request)
        {
            return request.Status switch
            {
                0 => DiscoveryFailureKind.Network,
                (int)HttpStatusCode.Unauthorized => DiscoveryFailureKind.Authentication,
                (int)HttpStatusCode.Forbidden => DiscoveryFailureKind.AccessDenied,
                (int)HttpStatusCode.RequestTimeout => DiscoveryFailureKind.Service,
                (int)HttpStatusCode.TooManyRequests => DiscoveryFailureKind.RateLimited,
                >= 500 => DiscoveryFailureKind.Service,
                _ => DiscoveryFailureKind.Unknown,
            };
        }
        if (ex is HttpRequestException) return DiscoveryFailureKind.Network;
        if (ex is TimeoutException or TaskCanceledException) return DiscoveryFailureKind.Service;
        return ex.InnerException is not null ? ClassifyFailure(ex.InnerException) : DiscoveryFailureKind.Unknown;
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

    internal static bool IsChatModel(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var lower = name.ToLowerInvariant();
        // Exclude transcription/image/embedding/etc.
        if (lower.Contains("transcrib") || lower.Contains("transcription") ||
            lower.Contains("image") || lower.Contains("embed") ||
            lower.Contains("voice") || lower.Contains("speech") || lower.Contains("audio") ||
            lower.Contains("dall-e") || lower.Contains("realtime") || lower.Contains("real-time") ||
            lower.Contains("whisper") || lower.Contains("tts") || lower.Contains("video") ||
            lower.Contains("moderation") || lower.Contains("rerank")) return false;
        return lower.Contains("gpt") || lower.StartsWith("o1") || lower.StartsWith("o3") || lower.StartsWith("o4") ||
               lower.Contains("deepseek") || lower.Contains("mai-ds") || lower.Contains("mai-1") || lower.Contains("phi");
    }

    internal static bool IsImageModel(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var lower = name.ToLowerInvariant();
        return lower.Contains("image") || lower.Contains("dall-e");
    }

    internal static bool IsTranscribeModel(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var lower = name.ToLowerInvariant();
        if (!lower.Contains("transcribe") && !lower.Contains("whisper")) return false;

        // Realtime-only models expose a websocket transcription session and do NOT
        // implement /audio/transcriptions, which is what OpenAITranscribeService posts
        // windowed chunks to. Selecting one would leave transcription permanently
        // broken, so exclude them until a realtime backend exists.
        return !IsRealtimeOnlyTranscribeModel(lower);
    }

    /// <summary>
    /// True for models whose only transcription surface is the realtime websocket API
    /// (Foundry capability <c>realtimeTranscription</c> without <c>audioTranscriptions</c>).
    /// </summary>
    internal static bool IsRealtimeOnlyTranscribeModel(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var lower = name.ToLowerInvariant();
        return lower.Contains("live-transcribe") || lower.Contains("realtime-whisper");
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

    /// <summary>
    /// Score for selecting a FAST chat model for realtime use.
    /// <para>
    /// Tier names are not a reliable proxy for latency. Measured against the live
    /// Responses API at effort=low on an identical prompt: terra 4.4 s, sol 8.1 s,
    /// luna 13.6 s — so the "light" tier was in fact the slowest of the three, and
    /// picking by name alone made mid-meeting updates lag worst. Ranking now
    /// reflects the measurement.
    /// </para>
    /// </summary>
    private static int FastChatScore(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return 0;
        var lower = modelName.ToLowerInvariant();
        // Prefer the fast sibling of the NEWEST family rather than an old small model:
        // a modern mid-tier beats a previous-generation mini on both latency and
        // structured-output fidelity.
        var (major, minor) = ParseGptVersion(lower);
        if (major >= 5)
        {
            var score = 500 + major * 20 + minor * 5;
            if (lower.Contains("terra")) score += 60;   // measured fastest
            else if (lower.Contains("luna")) score += 20;
            else if (lower.Contains("mini") || lower.Contains("flash") || lower.Contains("chat")) score += 40;
            if (lower.Contains("sol") || lower.Contains("pro"))
                score -= 30;           // top reasoning tiers are too slow for continuous mode
            return score;
        }

        if (lower.Contains("mini") || lower.Contains("flash") || lower.Contains("turbo")) return 90;
        if (lower.Contains("gpt-4o")) return 70;
        if (lower.Contains("chat")) return 60;
        if (lower.Contains("gpt-3.5")) return 50;
        if (lower.Contains("pro") || lower.StartsWith("o1") || lower.StartsWith("o3")) return 10;
        return 30;
    }

    /// <summary>Test seam for the fast-path ranking.</summary>
    internal static int FastChatScoreForTests(string? modelName) => FastChatScore(modelName);

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

    /// <summary>
    /// Ranks a transcription deployment. Newer generations first.
    /// <c>gpt-transcribe</c> (2026) supersedes the gpt-4o-transcribe family; the old
    /// rule matched only the explicit gpt-4o names, so a newer plain "gpt-transcribe"
    /// deployment fell through to the catch-all and scored BELOW whisper.
    /// </summary>
    internal static int TranscribeScore(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return 0;
        var lower = modelName.ToLowerInvariant();

        // Never rank a model the windowed /audio/transcriptions path cannot call.
        if (IsRealtimeOnlyTranscribeModel(lower)) return 0;

        if (lower.Contains("mai-transcribe")) return 120;
        // Plain "gpt-transcribe" is the current generation. Check it only after the
        // gpt-4o-* names so a substring can't steal their match.
        if (lower.Contains("gpt-4o-transcribe-diarize")) return 100;
        if (lower.Contains("gpt-4o-mini-transcribe")) return 80;
        if (lower.Contains("gpt-4o-transcribe")) return 90;
        if (lower.Contains("gpt-transcribe")) return 130;
        if (lower.Contains("transcribe")) return 60;
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
    string? AccountResourceId = null,
    DiscoveryFailureKind FailureKind = DiscoveryFailureKind.None);

public enum DiscoveryFailureKind
{
    None,
    Authentication,
    AccessDenied,
    Network,
    Service,
    RateLimited,
    Unknown,
}
