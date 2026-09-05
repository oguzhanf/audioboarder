using Azure.Core;
using AudioBoarder.App.Auth;
using AudioBoarder.App.Configuration;
using AudioBoarder.Services.LLM;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioBoarder.App.Setup;

public sealed partial class AzureSetupViewModel : ObservableObject, IDisposable
{
    private readonly IAzureModelInventory _inventory;
    private readonly IAzureCredentialProvider _credentials;
    private readonly AudioBoarderSettings _settings;
    private readonly IAzureProvisioningService? _provisioning;
    private CancellationTokenSource? _loadCancellation;
    private int _loadVersion;
    private bool _disposed;
    private bool _accountInventoryLoaded;

    public AzureSetupViewModel(
        IAzureModelInventory inventory,
        IAzureCredentialProvider credentials,
        AudioBoarderSettings settings,
        IAzureProvisioningService? provisioning = null)
    {
        _inventory = inventory;
        _credentials = credentials;
        _settings = settings;
        _provisioning = provisioning;
        transcriptionBackend = settings.CloudTranscription.Backend.ToLowerInvariant() switch
        {
            "cloud" or "openai" => "cloud",
            "local" or "whisper" => "local",
            "speech" => "speech",
            _ => "auto",
        };
        enableImages = settings.ImageGeneration.Enabled;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed), nameof(CanSave), nameof(IsIdle), nameof(CanProvision), nameof(CanDeploy))]
    private bool isBusy;

    [ObservableProperty] private string statusMessage = "Sign in, then choose a subscription and an Azure OpenAI or Foundry resource.";
    [ObservableProperty] private IReadOnlyList<AzureSubscriptionInfo> subscriptions = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeploy))]
    private IReadOnlyList<AzureAccountInfo> accounts = [];
    [ObservableProperty] private IReadOnlyList<AzureDeploymentInfo> chatDeployments = [];
    [ObservableProperty] private IReadOnlyList<AzureDeploymentChoice> transcriptionDeployments = [];
    [ObservableProperty] private IReadOnlyList<AzureDeploymentChoice> imageDeployments = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProvision), nameof(CanDeploy))]
    private AzureSubscriptionInfo? selectedSubscription;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanProceed), nameof(CanSave))]
    private AzureAccountInfo? selectedAccount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(SelectionSummary))]
    private AzureDeploymentInfo? selectedChat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(SelectionSummary))]
    private AzureDeploymentInfo? selectedFastChat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(SelectionSummary))]
    private AzureDeploymentChoice? selectedTranscription;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(SelectionSummary))]
    private AzureDeploymentChoice? selectedImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(SelectionSummary))]
    private string transcriptionBackend;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(SelectionSummary))]
    private bool enableImages;

    public string AccountSummary => _credentials.SignedInAs is { } username
        ? $"Signed in as {username} | Tenant: {(string.IsNullOrWhiteSpace(_credentials.TenantId) ? "default directory" : _credentials.TenantId)}"
        : "No verified Azure sign-in. Your Microsoft login may not include an Azure subscription.";

    public bool HasLoaded { get; private set; }
    public bool IsIdle => !IsBusy;
    public bool NeedsSetup { get; private set; } = true;
    public bool CanProceed => !IsBusy && SelectedAccount is { FailureKind: DiscoveryFailureKind.None } &&
                              ChatDeployments.Count > 0;
    public bool CanSave => !IsBusy && SelectionProblem is null;
    public bool CanProvision => _provisioning is not null && !IsBusy && SelectedSubscription is not null &&
                                MatchesSignedInTenant() && _credentials.TryGetSignedInCredential(out _);
    public bool CanDeploy => CanProvision && Accounts.Any(a => a.FailureKind == DiscoveryFailureKind.None);

    public AzureProvisioningViewModel CreateProvisioning(AzureModelRole? role = null)
    {
        if (!CanProvision)
            throw new InvalidOperationException("Sign in and select an accessible subscription before provisioning.");
        return new AzureProvisioningViewModel(_provisioning!, _credentials, SelectedSubscription!,
            Accounts, SelectedAccount, role);
    }

    public async Task SelectProvisionedAsync(AzureProvisioningViewModel provisioning, CancellationToken ct)
    {
        await RefreshAccountsAsync(ct);
        if (provisioning.CreatedResource is { } resource)
        {
            SelectedAccount = _accountInventoryLoaded ? Accounts.FirstOrDefault(a => a.Id == resource.Id) : null;
            StatusMessage = SelectedAccount is null
                ? "The resource was created but is not listed yet. Refresh shortly; do not create it again."
                : "Resource created. Deploy a chat, transcription or image model using the buttons in this wizard. Creation does not grant inference roles.";
        }
        else if (provisioning.CreatedDeployment is { } deployment)
        {
            var account = Accounts.FirstOrDefault(a => a.Id == provisioning.TargetAccount?.Id);
            var fresh = account?.Deployments.FirstOrDefault(d => d.Name == deployment.Name && d.IsReady);
            if (!_accountInventoryLoaded || account is null || fresh is null ||
                account.FailureKind != DiscoveryFailureKind.None)
            {
                StatusMessage = "Deployment succeeded but is not listed as ready yet. Refresh shortly; Azure resources remain even if you cancel setup.";
                return;
            }
            switch (provisioning.Role)
            {
                case AzureModelRole.Chat:
                    SelectedAccount = account;
                    SelectedChat = ChatDeployments.First(d => d.Name == fresh.Name);
                    break;
                case AzureModelRole.Transcription:
                    SelectedTranscription = TranscriptionDeployments.First(d => d.Account.Id == account.Id && d.Deployment.Name == fresh.Name);
                    TranscriptionBackend = "cloud";
                    break;
                case AzureModelRole.Image:
                    SelectedImage = ImageDeployments.First(d => d.Account.Id == account.Id && d.Deployment.Name == fresh.Name);
                    EnableImages = true;
                    break;
            }
            StatusMessage = "Model deployed and selected. Finish setup to save this choice. Azure resources remain even if you cancel setup.";
        }
    }

    public string? SelectionProblem
    {
        get
        {
            if (SelectedSubscription is null || SelectedAccount is null)
                return "Choose an accessible subscription and resource.";
            if (!_credentials.TryGetSignedInCredential(out _) || !MatchesSignedInTenant())
                return "Save the tenant change and restart, then sign in to the intended tenant.";
            if (SelectedAccount.FailureKind != DiscoveryFailureKind.None ||
                !Accounts.Contains(SelectedAccount))
                return "Deployment access is required for the selected resource.";
            if (!Uri.TryCreate(SelectedAccount.Endpoint, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps)
                return "The resource has no usable HTTPS endpoint.";
            if (SelectedChat is null || !ChatDeployments.Contains(SelectedChat))
                return "Deploy and select a supported chat model first.";
            if (SelectedFastChat is not null && !ChatDeployments.Contains(SelectedFastChat))
                return "The fast model must belong to the same resource as the primary model.";
            if (TranscriptionBackend == "cloud" && SelectedTranscription is null)
                return "Cloud transcription requires a deployed transcription model. Alternatively, choose local transcription.";
            if (TranscriptionBackend == "speech" &&
                (string.IsNullOrWhiteSpace(_settings.AzureSpeech.Region) ||
                 (string.IsNullOrWhiteSpace(_settings.AzureSpeech.ResourceId) &&
                  string.IsNullOrWhiteSpace(_settings.AzureSpeech.ApiKey))))
                return "Configure Azure Speech in Settings, or choose auto or local transcription here.";
            if (SelectedTranscription is not null && !TranscriptionDeployments.Contains(SelectedTranscription))
                return "Choose a transcription deployment from the current subscription.";
            if (EnableImages && (SelectedImage is null || !ImageDeployments.Contains(SelectedImage)))
                return "Image generation requires an image deployment, or turn image generation off.";
            return null;
        }
    }

    public string SelectionSummary =>
        $"Resource: {SelectedAccount?.DisplayName ?? "not selected"}\n" +
        $"Primary: {SelectedChat?.DisplayName ?? "not selected"}\n" +
        $"Fast / fallback: {SelectedFastChat?.DisplayName ?? "use primary model"}\n" +
        $"Transcription: {TranscriptionBackend}" +
        (TranscriptionBackend is "auto" or "cloud"
            ? $" / {SelectedTranscription?.DisplayName ?? "local Whisper fallback"}" : "") +
        $"\nImages: {(EnableImages ? SelectedImage?.DisplayName ?? "not selected" : "disabled")}";

    public Task RefreshAsync(CancellationToken ct = default)
    {
        var draft = CaptureDraft();
        return LoadAsync(async (credential, token) =>
        {
            var priorSubscriptionId = SelectedSubscription?.Id ?? _settings.AzureOpenAI.SubscriptionId;
            ClearAccounts();
            Subscriptions = [];
            SelectedSubscription = null;
            StatusMessage = "Looking for Azure subscriptions under this sign-in...";
            var result = await _inventory.ListSubscriptionsAsync(credential, token);
            token.ThrowIfCancellationRequested();
            Subscriptions = result.Subscriptions;
            if (result.FailureKind != DiscoveryFailureKind.None)
            {
                StatusMessage = FailureMessage(result.FailureKind, "subscriptions");
                return;
            }
            if (Subscriptions.Count == 0)
            {
                StatusMessage = "No Azure subscriptions are visible to this login. A Microsoft account alone is not an Azure subscription. Ask your administrator for access, or set up an Azure subscription.";
                return;
            }

            SelectedSubscription = Subscriptions.FirstOrDefault(s => s.Id == priorSubscriptionId);
            if (!string.IsNullOrWhiteSpace(priorSubscriptionId) && SelectedSubscription is null)
            {
                StatusMessage = "The saved subscription is not visible to this login. Select a subscription below, or ask an administrator for access in the correct tenant.";
                return;
            }
            SelectedSubscription ??= Subscriptions[0];
            await LoadSelectedAccountsAsync(credential, token, draft);
            // An unconfigured login can have several subscriptions. Do not mistake an
            // empty default subscription for a lack of usable resources in the tenant.
            if (string.IsNullOrWhiteSpace(priorSubscriptionId) && NeedsSetup)
            {
                foreach (var subscription in Subscriptions.Skip(1))
                {
                    SelectedSubscription = subscription;
                    await LoadSelectedAccountsAsync(credential, token, draft);
                    if (!NeedsSetup) break;
                }
            }
        }, ct);
    }

    public Task RefreshAccountsAsync(CancellationToken ct = default)
    {
        var draft = CaptureDraft();
        return LoadAsync((credential, token) => LoadSelectedAccountsAsync(credential, token, draft), ct);
    }

    public async Task SignInAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        if (!MatchesSignedInTenant())
        {
            StatusMessage = "Save the changed tenant with Save & Restart, then sign in to that tenant before selecting models.";
            return;
        }
        IsBusy = true;
        try
        {
            StatusMessage = "Complete Microsoft sign-in in your browser, then return to this wizard.";
            var result = await _credentials.SignInInteractiveAsync(ct);
            StatusMessage = result.Message;
            if (!result.Success) return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(AccountSummary));
        }
        if (_disposed || ct.IsCancellationRequested) return;
        await RefreshAsync(ct);
    }

    public AzureModelSelection GetSelection()
    {
        if (SelectionProblem is { } problem) throw new InvalidOperationException(problem);
        return new AzureModelSelection(
            _credentials.TenantId,
            SelectedSubscription!.Id,
            SelectedAccount!,
            SelectedChat!,
            SelectedFastChat,
            TranscriptionBackend,
            TranscriptionBackend is "auto" or "cloud" ? SelectedTranscription : null,
            EnableImages,
            EnableImages ? SelectedImage : null);
    }

    private bool MatchesSignedInTenant() =>
        string.IsNullOrWhiteSpace(_settings.AzureOpenAI.TenantId) ||
        string.Equals(_settings.AzureOpenAI.TenantId.Trim(), _credentials.TenantId?.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private async Task LoadAsync(
        Func<TokenCredential, CancellationToken, Task> load,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var version = ++_loadVersion;
        _loadCancellation?.Cancel();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        _loadCancellation = timeout;
        IsBusy = true;
        NeedsSetup = true;
        try
        {
            if (!_credentials.TryGetSignedInCredential(out var credential) || credential is null ||
                !MatchesSignedInTenant())
            {
                ClearAccounts();
                Subscriptions = [];
                SelectedSubscription = null;
                StatusMessage = "Sign in to the configured tenant to list resources. If you changed the tenant in Settings, save and restart first.";
                return;
            }
            await load(credential, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (version == _loadVersion && !ct.IsCancellationRequested)
            {
                ClearAccounts();
                StatusMessage = "Azure discovery timed out. Check the connection and select Refresh to retry.";
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                _loadCancellation = null;
                IsBusy = false;
                HasLoaded = true;
                OnPropertyChanged(nameof(AccountSummary));
                OnPropertyChanged(nameof(NeedsSetup));
            }
        }
    }

    private async Task LoadSelectedAccountsAsync(
        TokenCredential credential, CancellationToken ct, DraftSelection? draft)
    {
        var subscription = SelectedSubscription;
        ClearAccounts();
        if (subscription is null)
        {
            StatusMessage = "Select an Azure subscription to find resources.";
            return;
        }
        StatusMessage = "Looking for Azure OpenAI and Microsoft Foundry resources and deployed models...";
        var result = await _inventory.ListAccountsAsync(credential, subscription.Id, ct);
        ct.ThrowIfCancellationRequested();
        Accounts = result.Accounts;
        if (result.FailureKind != DiscoveryFailureKind.None)
        {
            StatusMessage = FailureMessage(result.FailureKind, "resources");
            return;
        }
        if (Accounts.Count == 0)
        {
            StatusMessage = "No Azure OpenAI or Microsoft Foundry (AIServices) resources were found in this subscription. Create one below, deploy a chat model, then select Refresh.";
            return;
        }
        _accountInventoryLoaded = true;
        var deployments = Accounts.Where(a => a.FailureKind == DiscoveryFailureKind.None)
            .SelectMany(a => a.Deployments.Where(d => d.IsReady).Select(d => new AzureDeploymentChoice(a, d)))
            .ToArray();
        TranscriptionDeployments = deployments.Where(d => d.Deployment.IsTranscription).ToArray();
        ImageDeployments = deployments.Where(d => d.Deployment.IsImage).ToArray();
        SelectedTranscription = FindChoice(
            TranscriptionDeployments, _settings.CloudTranscription.DeploymentName, _settings.CloudTranscription.Endpoint);
        SelectedImage = FindChoice(
            ImageDeployments, _settings.ImageGeneration.DeploymentName, _settings.ImageGeneration.Endpoint);
        if (draft is not null && draft.SubscriptionId == subscription.Id)
        {
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == draft.AccountId);
            SelectedChat = ChatDeployments.FirstOrDefault(d => d.Name == draft.ChatName);
            SelectedFastChat = ChatDeployments.FirstOrDefault(d => d.Name == draft.FastName);
            SelectedTranscription = TranscriptionDeployments.FirstOrDefault(d =>
                d.Account.Id == draft.Transcription?.Account.Id && d.Deployment.Name == draft.Transcription?.Deployment.Name);
            SelectedImage = ImageDeployments.FirstOrDefault(d =>
                d.Account.Id == draft.Image?.Account.Id && d.Deployment.Name == draft.Image?.Deployment.Name);
            if (SelectedAccount is null)
                StatusMessage = "The selected resource is no longer visible. Choose an accessible resource or restore its permissions.";
        }
        else
        {
            SelectedAccount = Accounts.FirstOrDefault(MatchesConfiguredAccount)
                              ?? Accounts.FirstOrDefault(a => a.FailureKind == DiscoveryFailureKind.None &&
                                                             a.Deployments.Any(d => d.IsReady && d.IsChat))
                              ?? Accounts[0];
        }
        NeedsSetup = !ConfigurationAvailable();
        if (NeedsSetup && SelectedAccount is { FailureKind: DiscoveryFailureKind.None } && ChatDeployments.Count > 0)
            StatusMessage = "A configured or required model is missing or unavailable. Choose replacement models; local transcription and disabled images do not require extra Azure deployments.";
    }

    partial void OnSelectedAccountChanged(AzureAccountInfo? value)
    {
        SelectedChat = null;
        SelectedFastChat = null;
        ChatDeployments = value is { FailureKind: DiscoveryFailureKind.None }
            ? value.Deployments.Where(d => d.IsReady && d.IsChat).ToArray() : [];
        SelectedChat = ChatDeployments.FirstOrDefault(d => d.Name == _settings.AzureOpenAI.DeploymentName)
                       ?? ChatDeployments.FirstOrDefault();
        SelectedFastChat = ChatDeployments.FirstOrDefault(d => d.Name == _settings.AzureOpenAI.FallbackDeploymentName);
        if (value is null) return;
        StatusMessage = value.FailureKind != DiscoveryFailureKind.None
            ? FailureMessage(value.FailureKind, "deployments in this resource")
            : ChatDeployments.Count == 0
                ? "The resource exists, but it has no ready, supported chat deployment. Deploy a chat model in Azure OpenAI or Foundry, wait for deployment to finish, then Refresh. Image, embedding and realtime-only models cannot generate diagrams."
                : $"Found {ChatDeployments.Count} chat deployment(s). Choose models next. Model visibility does not verify inference permissions or quota.";
        OnPropertyChanged(nameof(CanProceed));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private bool ConfigurationAvailable()
    {
        var azure = _settings.AzureOpenAI;
        var candidates = Accounts.Where(a => a.FailureKind == DiscoveryFailureKind.None);
        if (!string.IsNullOrWhiteSpace(azure.AccountResourceId) || !string.IsNullOrWhiteSpace(azure.Endpoint))
            candidates = candidates.Where(MatchesConfiguredAccount);
        if (!azure.AutoDiscover &&
            (string.IsNullOrWhiteSpace(azure.Endpoint) || string.IsNullOrWhiteSpace(azure.DeploymentName)))
            return false;
        if (!candidates.Any(a =>
                a.Deployments.Any(d => d.IsReady && d.IsChat &&
                    (string.IsNullOrWhiteSpace(azure.DeploymentName) || d.Name == azure.DeploymentName)) &&
                (string.IsNullOrWhiteSpace(azure.FallbackDeploymentName) ||
                 a.Deployments.Any(d => d.IsReady && d.IsChat && d.Name == azure.FallbackDeploymentName))))
            return false;
        if (TranscriptionBackend == "cloud" &&
            !RequiredRoleAvailable(TranscriptionDeployments, _settings.CloudTranscription.DeploymentName,
                _settings.CloudTranscription.Endpoint))
            return false;
        if (TranscriptionBackend == "speech" &&
            (string.IsNullOrWhiteSpace(_settings.AzureSpeech.Region) ||
             (string.IsNullOrWhiteSpace(_settings.AzureSpeech.ResourceId) &&
              string.IsNullOrWhiteSpace(_settings.AzureSpeech.ApiKey))))
            return false;
        return !EnableImages || RequiredRoleAvailable(ImageDeployments, _settings.ImageGeneration.DeploymentName,
            _settings.ImageGeneration.Endpoint);
    }

    private bool RequiredRoleAvailable(IReadOnlyList<AzureDeploymentChoice> choices, string? name, string? endpoint)
    {
        if (!_settings.AzureOpenAI.AutoDiscover)
        {
            endpoint ??= _settings.AzureOpenAI.Endpoint;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(endpoint))
                return false;
        }
        return FindChoice(choices, name, endpoint) is not null;
    }

    private DraftSelection? CaptureDraft() =>
        HasLoaded && SelectedAccount is { } account && SelectedSubscription is { } subscription &&
        account.Id.StartsWith($"/subscriptions/{subscription.Id}/", StringComparison.OrdinalIgnoreCase)
            ? new(subscription.Id, account.Id, SelectedChat?.Name, SelectedFastChat?.Name,
                SelectedTranscription, SelectedImage)
            : null;

    private sealed record DraftSelection(
        string SubscriptionId,
        string AccountId,
        string? ChatName,
        string? FastName,
        AzureDeploymentChoice? Transcription,
        AzureDeploymentChoice? Image);

    private bool MatchesConfiguredAccount(AzureAccountInfo account) =>
        !string.IsNullOrWhiteSpace(_settings.AzureOpenAI.AccountResourceId)
            ? string.Equals(account.Id, _settings.AzureOpenAI.AccountResourceId, StringComparison.OrdinalIgnoreCase)
            : !string.IsNullOrWhiteSpace(_settings.AzureOpenAI.Endpoint) &&
              SameEndpoint(account.Endpoint, _settings.AzureOpenAI.Endpoint);

    private static AzureDeploymentChoice? FindChoice(
        IReadOnlyList<AzureDeploymentChoice> choices, string? name, string? endpoint) =>
        choices.FirstOrDefault(c => (string.IsNullOrWhiteSpace(name) || c.Deployment.Name == name) &&
                                   (string.IsNullOrWhiteSpace(endpoint) || SameEndpoint(c.Account.Endpoint, endpoint)));

    private static bool SameEndpoint(string left, string right) =>
        string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private void ClearAccounts()
    {
        _accountInventoryLoaded = false;
        Accounts = [];
        SelectedAccount = null;
        ChatDeployments = [];
        SelectedChat = null;
        SelectedFastChat = null;
        TranscriptionDeployments = [];
        ImageDeployments = [];
        SelectedTranscription = null;
        SelectedImage = null;
    }

    private static string FailureMessage(DiscoveryFailureKind failure, string target) => failure switch
    {
        DiscoveryFailureKind.Authentication => "Azure requires sign-in again. Sign in and then Refresh.",
        DiscoveryFailureKind.AccessDenied =>
            $"Azure denied permission to list {target}. This does not mean they are missing. Ask an administrator for resource/deployment read access in the selected tenant and subscription.",
        DiscoveryFailureKind.Network => "Azure could not be reached. Check your connection or private-network access, then Refresh.",
        DiscoveryFailureKind.RateLimited => "Azure is throttling discovery. Wait briefly, then Refresh.",
        _ => "Azure could not complete discovery. Availability is unknown; no resources have been changed. Retry with Refresh.",
    };

    public void Dispose()
    {
        _disposed = true;
        _loadCancellation?.Cancel();
    }
}
