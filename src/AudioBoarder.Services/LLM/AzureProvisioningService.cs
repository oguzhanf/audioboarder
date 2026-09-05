using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Azure.ResourceManager.CognitiveServices;
using Azure.ResourceManager.CognitiveServices.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Models;

namespace AudioBoarder.Services.LLM;

public enum AzureModelRole { Chat, Transcription, Image }

public sealed record AzureRegionInfo(string Name, string DisplayName);
public sealed record AzureCreationContext(IReadOnlyList<string> ResourceGroups, IReadOnlyList<AzureRegionInfo> Regions);
public sealed record AzureModelQuota(string Name, double Current, double Limit);

public sealed record AzureModelSkuInfo(
    string Name, int Minimum, int Maximum, int Step, int DefaultCapacity,
    IReadOnlyList<int> AllowedValues, string? UsageName)
{
    public string DisplayName => $"{Name} (capacity {Minimum}-{Maximum})";
    public bool Accepts(int capacity) => capacity >= Minimum && capacity <= Maximum &&
        (AllowedValues.Count > 0 ? AllowedValues.Contains(capacity) : (capacity - Minimum) % Step == 0);
}

public sealed record AzureDeployableModel(
    string Format, string Name, string Version, AzureModelRole Role,
    IReadOnlyList<AzureModelSkuInfo> Skus)
{
    public string DisplayName => $"{Name} / {Version} ({Format})";
}

public sealed record AzureDeploymentCatalog(
    IReadOnlyList<AzureDeployableModel> Models, IReadOnlyList<AzureModelQuota> Quotas, string? QuotaMessage);
public sealed record AzureResourceCreateRequest(
    string SubscriptionId, string ResourceGroup, string Region, string Name, string Kind,
    bool CreateResourceGroup, bool PublicNetworkAccess, bool Confirmed);
public sealed record AzureDeploymentCreateRequest(
    string AccountResourceId, string Name, AzureDeployableModel Model,
    string Sku, int Capacity, bool Confirmed);

public interface IAzureProvisioningService
{
    Task<AzureCreationContext> GetCreationContextAsync(TokenCredential credential, string subscriptionId, CancellationToken ct = default);
    Task<AzureDeploymentCatalog> GetDeploymentCatalogAsync(TokenCredential credential, string accountId, CancellationToken ct = default);
    Task<AzureAccountInfo> CreateResourceAsync(TokenCredential credential, AzureResourceCreateRequest request, IProgress<string>? progress = null, CancellationToken ct = default);
    Task<AzureDeploymentInfo> DeployModelAsync(TokenCredential credential, AzureDeploymentCreateRequest request, IProgress<string>? progress = null, CancellationToken ct = default);
}

public sealed class AzureProvisioningService : IAzureProvisioningService
{
    private readonly Func<TokenCredential, ArmClientOptions, ArmClient> _clientFactory;
    private static readonly HashSet<string> OnDemandSkus = new(StringComparer.OrdinalIgnoreCase)
        { "Standard", "GlobalStandard", "DataZoneStandard", "Developer" };

    public AzureProvisioningService() : this((credential, options) => new ArmClient(credential, defaultSubscriptionId: null, options)) { }

    internal AzureProvisioningService(Func<TokenCredential, ArmClientOptions, ArmClient> clientFactory) =>
        _clientFactory = clientFactory;

    public async Task<AzureCreationContext> GetCreationContextAsync(
        TokenCredential credential, string subscriptionId, CancellationToken ct = default)
    {
        ValidateSubscription(subscriptionId);
        var subscription = Client(credential).GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{subscriptionId}"));
        var groups = new List<string>();
        await foreach (var group in subscription.GetResourceGroups().GetAllAsync(cancellationToken: ct))
            groups.Add(group.Data.Name);
        var regions = new List<AzureRegionInfo>();
        await foreach (var location in subscription.GetLocationsAsync(cancellationToken: ct))
            regions.Add(new AzureRegionInfo(location.Name, location.DisplayName ?? location.Name));
        return new(groups.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            regions.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<AzureDeploymentCatalog> GetDeploymentCatalogAsync(
        TokenCredential credential, string accountId, CancellationToken ct = default)
    {
        var id = ValidateAccountId(accountId);
        var arm = Client(credential);
        var account = (await arm.GetCognitiveServicesAccountResource(id).GetAsync(ct)).Value;
        var models = new List<AzureDeployableModel>();
        await foreach (var model in account.GetModelsAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(model.Name) || string.IsNullOrWhiteSpace(model.Format) ||
                string.IsNullOrWhiteSpace(model.Version) ||
                model.Deprecation?.InferenceOn <= DateTimeOffset.UtcNow ||
                string.Equals(model.LifecycleStatus?.ToString(), "Deprecated", StringComparison.OrdinalIgnoreCase))
                continue;
            var role = FoundryDiscovery.IsTranscribeModel(model.Name) ? AzureModelRole.Transcription
                : FoundryDiscovery.IsImageModel(model.Name) ? AzureModelRole.Image
                : FoundryDiscovery.IsChatModel(model.Name) ? AzureModelRole.Chat
                : (AzureModelRole?)null;
            if (role is null) continue;
            var skus = model.Skus
                .Where(s => OnDemandSkus.Contains(s.Name) && !(s.DeprecationOn <= DateTimeOffset.UtcNow))
                .Select(s => new AzureModelSkuInfo(
                    s.Name, Math.Max(1, s.Capacity?.Minimum ?? 1),
                    s.Capacity?.Maximum ?? model.MaxCapacity ?? int.MaxValue,
                    Math.Max(1, s.Capacity?.Step ?? 1), Math.Max(1, s.Capacity?.Default ?? s.Capacity?.Minimum ?? 1),
                    s.Capacity?.AllowedValues.ToArray() ?? [], s.UsageName))
                .Where(s => s.Maximum >= s.Minimum).ToArray();
            if (skus.Length > 0)
                models.Add(new(model.Format, model.Name, model.Version, role.Value, skus));
        }

        var quotas = new List<AzureModelQuota>();
        string? quotaMessage = null;
        try
        {
            var subscription = arm.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{id.SubscriptionId}"));
            await foreach (var usage in subscription.GetUsagesAsync(account.Data.Location, cancellationToken: ct))
            {
                if (usage.Name?.Value is { } name && usage.Limit is { } limit && usage.CurrentValue is { } current)
                    quotas.Add(new(name, current, limit));
            }
        }
        catch (RequestFailedException ex) when (ex.Status is 403 or 404)
        {
            quotaMessage = "Quota could not be read. Azure will validate available quota when you deploy.";
        }
        return new(models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(m => m.Version).ToArray(),
            quotas, quotaMessage);
    }

    public async Task<AzureAccountInfo> CreateResourceAsync(
        TokenCredential credential, AzureResourceCreateRequest request,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireConfirmation(request.Confirmed);
        ValidateSubscription(request.SubscriptionId);
        if (!Regex.IsMatch(request.ResourceGroup, @"^[a-zA-Z0-9_().-]{1,90}$") || request.ResourceGroup.EndsWith('.'))
            throw new ArgumentException("Use a valid resource group name (1-90 letters, numbers, hyphens, underscores, periods or parentheses).");
        if (!Regex.IsMatch(request.Name, @"^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$"))
            throw new ArgumentException("Use a globally unique resource name: 3-64 lowercase letters, numbers or hyphens.");
        if (request.Kind is not ("OpenAI" or "AIServices"))
            throw new ArgumentException("Choose Azure OpenAI or Foundry (AIServices).");
        if (!Regex.IsMatch(request.Region, @"^[a-z0-9]+$"))
            throw new ArgumentException("Choose a valid Azure region.");
        ct.ThrowIfCancellationRequested();
        var arm = Client(credential);
        var subscription = arm.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{request.SubscriptionId}"));
        var groups = subscription.GetResourceGroups();
        var groupId = ResourceGroupResource.CreateResourceIdentifier(request.SubscriptionId, request.ResourceGroup);
        var group = arm.GetResourceGroupResource(groupId);
        var accounts = group.GetCognitiveServicesAccounts();
        if ((await accounts.ExistsAsync(request.Name, ct)).Value)
            throw new InvalidOperationException("That resource already exists. Select it in the wizard instead; existing resources are never intentionally overwritten.");
        if (!(await groups.ExistsAsync(request.ResourceGroup, ct)).Value)
        {
            if (!request.CreateResourceGroup)
                throw new InvalidOperationException("The resource group does not exist. Select an existing group or enable creation of a new group.");
            progress?.Report("Creating the resource group...");
            await groups.CreateOrUpdateAsync(WaitUntil.Completed, request.ResourceGroup,
                new ResourceGroupData(new AzureLocation(request.Region)), ct);
        }
        progress?.Report("Creating the Azure resource. This can take several minutes...");
        var data = new CognitiveServicesAccountData(new AzureLocation(request.Region))
        {
            Kind = request.Kind,
            Sku = new CognitiveServicesSku("S0"),
            Identity = request.Kind == "AIServices"
                ? new ManagedServiceIdentity(ManagedServiceIdentityType.SystemAssigned) : null,
            Properties = new CognitiveServicesAccountProperties
            {
                CustomSubDomainName = request.Name,
                AllowProjectManagement = request.Kind == "AIServices",
                DisableLocalAuth = true,
                PublicNetworkAccess = request.PublicNetworkAccess
                    ? ServiceAccountPublicNetworkAccess.Enabled : ServiceAccountPublicNetworkAccess.Disabled,
            },
        };
        data.Tags["created-by"] = "AudioBoarder";
        var operation = await accounts.CreateOrUpdateAsync(WaitUntil.Started, request.Name, data, ct);
        var created = (await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(2), ct)).Value;
        RequireSucceeded(created.Data.Properties?.ProvisioningState?.ToString());
        return new(created.Id.ToString(), created.Data.Name, created.Data.Kind,
            created.Data.Properties?.Endpoint ?? "", created.Data.Location.Name, []);
    }

    public async Task<AzureDeploymentInfo> DeployModelAsync(
        TokenCredential credential, AzureDeploymentCreateRequest request,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireConfirmation(request.Confirmed);
        var id = ValidateAccountId(request.AccountResourceId);
        if (!Regex.IsMatch(request.Name, @"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$"))
            throw new ArgumentException("Use a deployment name of 1-64 letters, numbers, periods, underscores or hyphens.");
        ArgumentNullException.ThrowIfNull(request.Model);
        ct.ThrowIfCancellationRequested();
        var arm = Client(credential);
        var deployments = arm.GetCognitiveServicesAccountResource(id).GetCognitiveServicesAccountDeployments();
        if ((await deployments.ExistsAsync(request.Name, ct)).Value)
            throw new InvalidOperationException("A deployment with that name already exists. Choose another name; existing deployments are not replaced.");
        progress?.Report("Checking model availability, SKU and quota in the resource's region...");
        var catalog = await GetDeploymentCatalogAsync(credential, request.AccountResourceId, ct);
        var model = catalog.Models.FirstOrDefault(m => m.Format == request.Model.Format &&
            m.Name == request.Model.Name && m.Version == request.Model.Version);
        var sku = model?.Skus.FirstOrDefault(s => s.Name == request.Sku);
        if (model is null || sku is null)
            throw new InvalidOperationException("That model version or SKU is no longer available in this resource. Refresh the model catalog.");
        if (!sku.Accepts(request.Capacity))
            throw new ArgumentException("The requested capacity is not an allowed value for this model and SKU.");
        var quota = catalog.Quotas.FirstOrDefault(q => string.Equals(q.Name, sku.UsageName, StringComparison.OrdinalIgnoreCase));
        if (quota is not null && request.Capacity > quota.Limit - quota.Current)
            throw new InvalidOperationException("There is not enough remaining quota for this capacity. Reduce capacity, choose another model/region, or request quota from Azure.");
        var data = new CognitiveServicesAccountDeploymentData
        {
            Sku = new CognitiveServicesSku(sku.Name) { Capacity = request.Capacity },
            Properties = new CognitiveServicesAccountDeploymentProperties
            {
                Model = new CognitiveServicesAccountDeploymentModel
                    { Format = model.Format, Name = model.Name, Version = model.Version },
            },
        };
        progress?.Report("Deploying the model. Keep this dialog open while Azure finishes...");
        var operation = await deployments.CreateOrUpdateAsync(WaitUntil.Started, request.Name, data, ct);
        var created = (await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(2), ct)).Value;
        RequireSucceeded(created.Data.Properties?.ProvisioningState?.ToString());
        return new(created.Data.Name, model.Name, model.Version,
            model.Role == AzureModelRole.Chat, model.Role == AzureModelRole.Transcription, model.Role == AzureModelRole.Image);
    }

    private ArmClient Client(TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var options = new ArmClientOptions { Retry = { MaxRetries = 0 } };
        options.AddPolicy(new CreateOnlyPolicy(), HttpPipelinePosition.PerCall);
        return _clientFactory(credential, options);
    }

    private static void RequireConfirmation(bool confirmed)
    {
        if (!confirmed) throw new InvalidOperationException("Confirm the Azure resource/deployment and its potential charges before creating it.");
    }

    private static void RequireSucceeded(string? state)
    {
        if (!string.Equals(state, "Succeeded", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Azure has not reported successful provisioning. Refresh inventory before retrying; resources may already exist.");
    }

    private static void ValidateSubscription(string? id)
    {
        if (!Guid.TryParse(id, out _)) throw new ArgumentException("Select a valid Azure subscription.");
    }

    private static ResourceIdentifier ValidateAccountId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var resource = new ResourceIdentifier(id);
        if (resource.ResourceType != new ResourceType("Microsoft.CognitiveServices/accounts"))
            throw new ArgumentException("Select an Azure OpenAI or Foundry account resource.");
        ValidateSubscription(resource.SubscriptionId);
        return resource;
    }

    private sealed class CreateOnlyPolicy : HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(HttpMessage message)
        {
            if (message.Request.Method == RequestMethod.Put)
                message.Request.Headers.SetValue("If-None-Match", "*");
        }
    }
}
