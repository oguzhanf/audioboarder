using System.Net.Http;
using Azure;
using Azure.Core;
using Azure.Identity;
using AudioBoarder.App.Auth;
using AudioBoarder.Services.LLM;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioBoarder.App.Setup;

public sealed partial class AzureProvisioningViewModel : ObservableObject, IDisposable
{
    private readonly IAzureProvisioningService _service;
    private readonly IAzureCredentialProvider _credentials;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private (string? User, string? Tenant)? _loadedIdentity;
    private IReadOnlyList<AzureModelQuota> _quotas = [];
    private string? _quotaMessage;
    private bool _disposed;

    public AzureProvisioningViewModel(
        IAzureProvisioningService service, IAzureCredentialProvider credentials,
        AzureSubscriptionInfo subscription, IReadOnlyList<AzureAccountInfo> accounts,
        AzureAccountInfo? account, AzureModelRole? role)
    {
        _service = service;
        _credentials = credentials;
        Subscription = subscription;
        Accounts = accounts.Where(a => a.FailureKind == DiscoveryFailureKind.None).ToArray();
        targetAccount = account ?? Accounts.FirstOrDefault();
        Role = role;
        name = role is null ? $"audioboarder-{Guid.NewGuid():N}"[..25] : "";
    }

    public AzureSubscriptionInfo Subscription { get; }
    public IReadOnlyList<AzureAccountInfo> Accounts { get; }
    public AzureModelRole? Role { get; }
    public bool IsResourceCreation => Role is null;
    public bool IsDeployment => !IsResourceCreation;
    public string Title => IsResourceCreation ? "Create Azure AI resource" : $"Deploy {Role!.Value.ToString().ToLowerInvariant()} model";
    public string AccountSummary => $"Signed in as {_credentials.SignedInAs ?? "not signed in"}";
    public string SubscriptionSummary => Subscription.DisplayName;
    public bool IsIdle => !IsBusy;
    public AzureAccountInfo? CreatedResource { get; private set; }
    public AzureDeploymentInfo? CreatedDeployment { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanCreate))]
    private bool isBusy;
    [ObservableProperty] private bool isCreating;
    [ObservableProperty] private string statusMessage = "Loading Azure options...";
    [ObservableProperty] private IReadOnlyList<string> resourceGroups = [];
    [ObservableProperty] private IReadOnlyList<AzureRegionInfo> regions = [];
    [ObservableProperty] private IReadOnlyList<AzureDeployableModel> models = [];
    [ObservableProperty] private IReadOnlyList<AzureModelSkuInfo> skus = [];
    [ObservableProperty] private AzureAccountInfo? targetAccount;
    [ObservableProperty] private AzureRegionInfo? selectedRegion;
    [ObservableProperty] private AzureDeployableModel? selectedModel;
    [ObservableProperty] private AzureModelSkuInfo? selectedSku;
    [ObservableProperty] private string resourceGroup = "";
    [ObservableProperty] private string kind = "AIServices";
    [ObservableProperty] private string name;
    [ObservableProperty] private int capacity = 1;
    [ObservableProperty] private bool createResourceGroup;
    [ObservableProperty] private bool publicNetworkAccess;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private bool confirmed;

    public bool CanCreate => !IsBusy && _loadedIdentity.HasValue && Confirmed && !string.IsNullOrWhiteSpace(Name) &&
        (IsResourceCreation
            ? SelectedRegion is not null && !string.IsNullOrWhiteSpace(ResourceGroup)
            : SelectedModel is not null && SelectedSku is not null && SelectedSku.Accepts(Capacity));

    public string QuotaSummary
    {
        get
        {
            if (SelectedSku is null) return "Select a model and SKU to see capacity limits.";
            var quota = _quotas.FirstOrDefault(q => string.Equals(q.Name, SelectedSku.UsageName, StringComparison.OrdinalIgnoreCase));
            var remaining = quota is null
                ? _quotaMessage ?? "Remaining quota is not reported for this SKU; Azure validates it at deployment time."
                : $"Quota used: {quota.Current:g} / {quota.Limit:g}; remaining: {Math.Max(0, quota.Limit - quota.Current):g}.";
            return $"Capacity range: {SelectedSku.Minimum}-{SelectedSku.Maximum}, step {SelectedSku.Step}. " +
                (SelectedSku.AllowedValues.Count > 0 ? $"Allowed: {string.Join(", ", SelectedSku.AllowedValues)}. " : "") +
                remaining + " Capacity units differ by model and are not a universal tokens-per-minute value.";
        }
    }

    public string ReviewSummary => IsResourceCreation
        ? $"Create {Kind} resource '{Name}' in {Subscription.Name} / {ResourceGroup}, region {SelectedRegion?.Name ?? "not selected"}, SKU S0. " +
          $"Resource group: {(CreateResourceGroup ? "create if missing" : "must already exist")}. " +
          $"Public network: {(PublicNetworkAccess ? "enabled" : "disabled - private connectivity must be configured before inference")}. " +
          "API-key authentication is disabled; the signed-in identity is used."
        : $"Deploy {SelectedModel?.DisplayName ?? "a model"} as '{Name}' in {TargetAccount?.Name ?? "a resource"} " +
          $"({TargetAccount?.Region}), SKU {SelectedSku?.Name ?? "not selected"}, capacity {Capacity}.";

    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Confirmed = false;
        _loadedIdentity = null;
        if (IsResourceCreation)
        {
            Regions = [];
            SelectedRegion = null;
            ResourceGroups = [];
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var credential = SignedInCredential();
            var identity = (_credentials.SignedInAs, _credentials.TenantId);
            StatusMessage = "Reading available Azure options using your sign-in...";
            if (IsResourceCreation)
            {
                var context = await _service.GetCreationContextAsync(credential, Subscription.Id, timeout.Token);
                ResourceGroups = context.ResourceGroups;
                Regions = context.Regions;
                ResourceGroup = ResourceGroups.FirstOrDefault() ?? "";
                SelectedRegion = Regions.FirstOrDefault(r => r.Name == TargetAccount?.Region) ?? Regions.FirstOrDefault();
                StatusMessage = "Choose the resource group, region, resource type and network access. Azure validates regional availability and policy.";
            }
            else if (TargetAccount is { } account)
            {
                Models = [];
                SelectedModel = null;
                var catalog = await _service.GetDeploymentCatalogAsync(credential, account.Id, timeout.Token);
                _quotas = catalog.Quotas;
                _quotaMessage = catalog.QuotaMessage;
                Models = catalog.Models.Where(m => m.Role == Role).ToArray();
                SelectedModel = Models.FirstOrDefault();
                StatusMessage = Models.Count == 0
                    ? $"No compatible on-demand {Role!.Value.ToString().ToLowerInvariant()} model/SKU is offered for this resource in {account.Region}. Choose another resource or create one in another region."
                    : "Choose a model version, deployment name, SKU and capacity. Provisioned-throughput and batch-only SKUs are not offered.";
            }
            else StatusMessage = "Choose an existing Azure AI resource, or create one from the Services step.";
            _loadedIdentity = identity;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            StatusMessage = "Loading was cancelled or timed out. Retry when connected to Azure.";
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            StatusMessage = DescribeFailure(ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(QuotaSummary));
        }
    }

    public async Task<bool> CreateAsync()
    {
        if (!CanCreate)
        {
            StatusMessage = "Complete the fields and explicitly confirm the Azure creation and potential charges.";
            return false;
        }
        IsBusy = true;
        IsCreating = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        _operation = timeout;
        try
        {
            if (_loadedIdentity != (_credentials.SignedInAs, _credentials.TenantId))
                throw new InvalidOperationException("The signed-in account changed. Reload the options before creating resources.");
            var credential = SignedInCredential();
            var progress = new Progress<string>(message => StatusMessage = message);
            if (IsResourceCreation)
                CreatedResource = await _service.CreateResourceAsync(credential,
                    new(Subscription.Id, ResourceGroup.Trim(), SelectedRegion!.Name, Name.Trim(), Kind,
                        CreateResourceGroup, PublicNetworkAccess, Confirmed), progress, timeout.Token);
            else
                CreatedDeployment = await _service.DeployModelAsync(credential,
                    new(TargetAccount!.Id, Name.Trim(), SelectedModel!, SelectedSku!.Name, Capacity, Confirmed),
                    progress, timeout.Token);
            StatusMessage = "Azure reported successful provisioning.";
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            StatusMessage = "Stopped waiting. Azure may still finish the submitted operation; nothing was deleted or rolled back. Refresh inventory before trying again.";
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            StatusMessage = DescribeFailure(ex) + " Refresh inventory before retrying if Azure may have accepted the request.";
        }
        finally
        {
            _operation = null;
            IsCreating = false;
            IsBusy = false;
            Confirmed = false;
        }
        return false;
    }

    public void StopWaiting() => _operation?.Cancel();

    private TokenCredential SignedInCredential()
    {
        if (!_credentials.TryGetSignedInCredential(out var credential) || credential is null)
            throw new InvalidOperationException("Sign in to Azure in the setup wizard before creating resources.");
        return credential;
    }

    partial void OnTargetAccountChanged(AzureAccountInfo? value)
    {
        Models = [];
        SelectedModel = null;
        Changed();
    }
    partial void OnSelectedModelChanged(AzureDeployableModel? value)
    {
        Skus = value?.Skus ?? [];
        SelectedSku = Skus.FirstOrDefault();
        if (value is not null) Name = value.Name;
        Changed();
    }
    partial void OnSelectedSkuChanged(AzureModelSkuInfo? value)
    {
        if (value is not null)
            Capacity = value.AllowedValues.FirstOrDefault(n => n >= value.Minimum && n <= value.Maximum,
                Math.Clamp(value.DefaultCapacity, value.Minimum, value.Maximum));
        Changed();
        OnPropertyChanged(nameof(QuotaSummary));
    }
    partial void OnSelectedRegionChanged(AzureRegionInfo? value) => Changed();
    partial void OnResourceGroupChanged(string value) => Changed();
    partial void OnKindChanged(string value) => Changed();
    partial void OnNameChanged(string value) => Changed();
    partial void OnCapacityChanged(int value) => Changed();
    partial void OnCreateResourceGroupChanged(bool value) => Changed();
    partial void OnPublicNetworkAccessChanged(bool value) => Changed();
    private void Changed()
    {
        Confirmed = false;
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(ReviewSummary));
    }

    private static bool IsExpectedFailure(Exception ex) =>
        ex is RequestFailedException or AuthenticationFailedException or CredentialUnavailableException or
            HttpRequestException or TimeoutException or ArgumentException or InvalidOperationException;

    internal static string DescribeFailure(Exception ex)
    {
        if (ex is RequestFailedException azure)
        {
            var code = new string((azure.ErrorCode ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(100).ToArray());
            if (code.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                code.Contains("capacity", StringComparison.OrdinalIgnoreCase))
                return $"Azure quota/capacity is insufficient or unavailable ({code}). Reduce capacity, choose another region/model or request quota.";
            if (code.Contains("policy", StringComparison.OrdinalIgnoreCase))
                return $"Azure Policy denied this configuration ({code}). Choose an approved region, SKU and network configuration or ask your administrator.";
            if (code.Contains("registration", StringComparison.OrdinalIgnoreCase))
                return $"The Azure resource provider is not registered or registration is blocked ({code}). Ask an administrator to enable Microsoft.CognitiveServices for this subscription.";
            return azure.Status switch
            {
                >= 200 and < 300 => "Azure reported a failed or cancelled provisioning operation. Refresh inventory and review the resource's deployment details before retrying.",
                401 => "Azure requires sign-in again. Return to the wizard and sign in.",
                403 => "Azure denied this operation. Your account needs resource/deployment write permission in this subscription or resource group. No permissions are granted automatically.",
                409 or 412 => $"Azure reported a conflict ({code}). The name may already exist or another operation is running; choose a unique name or refresh.",
                429 => "Azure is throttling requests. Wait before retrying.",
                _ => $"Azure could not complete the operation (HTTP {azure.Status}, {code}). Check region/model availability, provider registration, Azure Policy, marketplace terms and quota.",
            };
        }
        return ex switch
        {
            AuthenticationFailedException or CredentialUnavailableException => "Azure authentication is unavailable. Sign in again.",
            HttpRequestException or TimeoutException => "Azure could not be reached or timed out. Check network/private endpoint access.",
            ArgumentException or InvalidOperationException => ex.Message,
            _ => "Azure provisioning failed.",
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
