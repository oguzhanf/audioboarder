using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.CognitiveServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http;

namespace AudioBoarder.Services.LLM;

public interface IAzureModelInventory
{
    Task<AzureSubscriptionInventory> ListSubscriptionsAsync(
        TokenCredential credential, CancellationToken ct = default);

    Task<AzureAccountInventory> ListAccountsAsync(
        TokenCredential credential, string subscriptionId, CancellationToken ct = default);
}

/// <summary>
/// Lists ARM resources using only the caller's verified credential. Does not sign in,
/// provision resources, or select deployments on the caller's behalf.
/// </summary>
public sealed class AzureModelInventory : IAzureModelInventory
{
    private readonly Func<TokenCredential, ArmClient> _armClientFactory;
    private readonly ILogger<AzureModelInventory> _logger;

    public AzureModelInventory(ILogger<AzureModelInventory>? logger = null)
        : this(credential => new ArmClient(credential), logger)
    {
    }

    internal AzureModelInventory(
        Func<TokenCredential, ArmClient> armClientFactory,
        ILogger<AzureModelInventory>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(armClientFactory);
        _armClientFactory = armClientFactory;
        _logger = logger ?? NullLogger<AzureModelInventory>.Instance;
    }

    public async Task<AzureSubscriptionInventory> ListSubscriptionsAsync(
        TokenCredential credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ct.ThrowIfCancellationRequested();
        var subscriptions = new List<AzureSubscriptionInfo>();
        try
        {
            var arm = _armClientFactory(credential);
            await foreach (var subscription in arm.GetSubscriptions().GetAllAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                subscriptions.Add(new AzureSubscriptionInfo(
                    subscription.Data.SubscriptionId ?? subscription.Id.Name,
                    subscription.Data.DisplayName ?? string.Empty));
            }
            ct.ThrowIfCancellationRequested();
            return new AzureSubscriptionInventory(subscriptions.ToArray());
        }
        catch (Exception ex) when (IsAzureFailure(ex))
        {
            var (kind, message) = DescribeFailure(ex, "subscriptions", ct);
            return new AzureSubscriptionInventory(subscriptions.ToArray(), kind, message);
        }
    }

    public async Task<AzureAccountInventory> ListAccountsAsync(
        TokenCredential credential, string subscriptionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ct.ThrowIfCancellationRequested();
        var accounts = new List<AzureAccountInfo>();
        try
        {
            var arm = _armClientFactory(credential);
            var subscription = arm.GetSubscriptionResource(
                new ResourceIdentifier($"/subscriptions/{subscriptionId}"));
            await foreach (var account in subscription.GetCognitiveServicesAccountsAsync(
                cancellationToken: ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var data = account.Data;
                if (!string.Equals(data.Kind, "OpenAI", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(data.Kind, "AIServices", StringComparison.OrdinalIgnoreCase))
                    continue;

                var deployments = new List<AzureDeploymentInfo>();
                var failureKind = DiscoveryFailureKind.None;
                string? message = null;
                try
                {
                    await foreach (var deployment in account.GetCognitiveServicesAccountDeployments()
                        .GetAllAsync(cancellationToken: ct).ConfigureAwait(false))
                    {
                        ct.ThrowIfCancellationRequested();
                        var properties = deployment.Data.Properties;
                        var modelName = properties?.Model?.Name ?? string.Empty;
                        var state = properties?.ProvisioningState?.ToString();
                        deployments.Add(new AzureDeploymentInfo(
                            deployment.Data.Name,
                            modelName,
                            properties?.Model?.Version,
                            FoundryDiscovery.IsChatModel(modelName),
                            FoundryDiscovery.IsTranscribeModel(modelName),
                            FoundryDiscovery.IsImageModel(modelName),
                            // Older API responses may omit the provisioning state.
                            IsReady: string.IsNullOrWhiteSpace(state) ||
                                     string.Equals(state, "Succeeded", StringComparison.OrdinalIgnoreCase)));
                    }
                }
                catch (Exception ex) when (IsAzureFailure(ex))
                {
                    (failureKind, message) = DescribeFailure(ex, "deployments for this resource", ct);
                }

                ct.ThrowIfCancellationRequested();
                accounts.Add(new AzureAccountInfo(
                    account.Id.ToString(),
                    data.Name,
                    data.Kind,
                    data.Properties?.Endpoint?.ToString() ?? string.Empty,
                    data.Location.Name ?? string.Empty,
                    deployments.ToArray(),
                    failureKind,
                    message));
            }
            ct.ThrowIfCancellationRequested();
            // Deployment-list failures belong to their resource; the account list itself succeeded.
            return new AzureAccountInventory(accounts.ToArray());
        }
        catch (Exception ex) when (IsAzureFailure(ex))
        {
            var (kind, message) = DescribeFailure(ex, "resources in this subscription", ct);
            return new AzureAccountInventory(accounts.ToArray(), kind, message);
        }
    }

    private static bool IsAzureFailure(Exception ex) =>
        ex is AuthenticationRequiredException or CredentialUnavailableException or AuthenticationFailedException
            or RequestFailedException or HttpRequestException or TimeoutException or TaskCanceledException;

    private (DiscoveryFailureKind Kind, string Message) DescribeFailure(
        Exception ex, string scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var kind = FoundryDiscovery.ClassifyFailure(ex);
        _logger.LogWarning("Azure inventory could not list {Scope}; failure={FailureKind}", scope, kind);
        var message = kind switch
        {
            DiscoveryFailureKind.Authentication =>
                $"Azure could not authenticate the supplied credential to list {scope}. Sign in again and retry.",
            DiscoveryFailureKind.AccessDenied =>
                $"Azure denied permission to list {scope}.",
            DiscoveryFailureKind.Network =>
                $"Azure could not list {scope} because the network request failed. Check your connection and retry.",
            DiscoveryFailureKind.Service =>
                $"Azure could not list {scope} because the service is unavailable or timed out. Retry shortly.",
            DiscoveryFailureKind.RateLimited =>
                $"Azure temporarily limited requests to list {scope}. Wait a moment and retry.",
            _ => $"Azure could not list {scope}. Try again.",
        };
        return (kind, message);
    }
}

public sealed record AzureSubscriptionInfo(string Id, string Name)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : $"{Name} ({Id})";
}

public sealed record AzureDeploymentInfo(
    string Name,
    string ModelName,
    string? Version,
    bool IsChat,
    bool IsTranscription,
    bool IsImage,
    bool IsReady = true)
{
    public string DisplayName =>
        $"{Name} ({(string.IsNullOrWhiteSpace(ModelName) ? "unknown model" : ModelName)}" +
        $"{(string.IsNullOrWhiteSpace(Version) ? "" : $", {Version}")})" +
        (IsReady ? "" : " — not ready");
}

public sealed record AzureAccountInfo(
    string Id,
    string Name,
    string Kind,
    string Endpoint,
    string Region,
    IReadOnlyList<AzureDeploymentInfo> Deployments,
    DiscoveryFailureKind FailureKind = DiscoveryFailureKind.None,
    string? Message = null)
{
    public string DisplayName =>
        $"{Name} ({Kind}{(string.IsNullOrWhiteSpace(Region) ? "" : $", {Region}")})";
}

public sealed record AzureSubscriptionInventory(
    IReadOnlyList<AzureSubscriptionInfo> Subscriptions,
    DiscoveryFailureKind FailureKind = DiscoveryFailureKind.None,
    string? Message = null);

public sealed record AzureAccountInventory(
    IReadOnlyList<AzureAccountInfo> Accounts,
    DiscoveryFailureKind FailureKind = DiscoveryFailureKind.None,
    string? Message = null);
