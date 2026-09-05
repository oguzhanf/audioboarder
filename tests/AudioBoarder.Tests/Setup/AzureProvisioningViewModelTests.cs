using Azure;
using Azure.Core;
using AudioBoarder.App.Setup;
using AudioBoarder.Services.LLM;

namespace AudioBoarder.Tests.Setup;

public sealed class AzureProvisioningViewModelTests
{
    [Fact]
    public async Task LoadingCreationOptionsDoesNotCreateAnything()
    {
        var service = new ProvisioningFake();
        using var vm = Create(service);

        await vm.LoadAsync();

        service.Writes.Should().Be(0);
        vm.CanCreate.Should().BeFalse();
        vm.Regions.Should().ContainSingle();
    }

    [Fact]
    public async Task UnconfirmedCreationDoesNotCallAzureWrite()
    {
        var service = new ProvisioningFake();
        using var vm = Create(service);
        await vm.LoadAsync();

        (await vm.CreateAsync()).Should().BeFalse();
        service.Writes.Should().Be(0);
        vm.StatusMessage.Should().Contain("explicitly confirm");
    }

    [Fact]
    public async Task ChangingAnyDeploymentParameterRequiresNewConfirmation()
    {
        using var vm = Create(new ProvisioningFake(), AzureModelRole.Transcription);
        await vm.LoadAsync();
        vm.Confirmed = true;
        vm.CanCreate.Should().BeTrue();

        vm.Capacity = 2;

        vm.Confirmed.Should().BeFalse();
        vm.CanCreate.Should().BeFalse();
    }

    [Fact]
    public async Task DeploymentUsesTheSignedInCredentialAndSelectedRoleModel()
    {
        var service = new ProvisioningFake();
        using var vm = Create(service, AzureModelRole.Transcription);
        await vm.LoadAsync();
        vm.Name = "meeting-audio";
        vm.Confirmed = true;

        (await vm.CreateAsync()).Should().BeTrue();

        service.Writes.Should().Be(1);
        service.Credential.Should().NotBeNull();
        service.Deployment!.Name.Should().Be("meeting-audio");
        service.Deployment.Model.Name.Should().Be("MAI-Transcribe-1");
        vm.CreatedDeployment!.IsTranscription.Should().BeTrue();
    }

    [Fact]
    public async Task EmptyCatalogExplainsRegionalAvailability()
    {
        var service = new ProvisioningFake { NoModels = true };
        using var vm = Create(service, AzureModelRole.Image);

        await vm.LoadAsync();

        vm.Models.Should().BeEmpty();
        vm.StatusMessage.Should().Contain("another region");
        vm.Confirmed = true;
        vm.CanCreate.Should().BeFalse();
    }

    [Fact]
    public async Task SignedOutIdentityCannotLoadOrProvisionResources()
    {
        var service = new ProvisioningFake();
        using var vm = new AzureProvisioningViewModel(service, new SetupCredentials { SignedIn = false },
            new("sub", "Subscription"), [], null, null);

        await vm.LoadAsync();
        vm.Confirmed = true;
        await vm.CreateAsync();

        service.Reads.Should().Be(0);
        service.Writes.Should().Be(0);
        vm.StatusMessage.Should().Contain("fields");
    }

    [Fact]
    public async Task AzurePermissionFailureIsVisibleWithoutSuccessOrRawPayload()
    {
        var service = new ProvisioningFake { Failure = new RequestFailedException(403, "secret diagnostic") };
        using var vm = Create(service);
        await vm.LoadAsync();
        vm.Confirmed = true;

        (await vm.CreateAsync()).Should().BeFalse();

        vm.CreatedResource.Should().BeNull();
        vm.StatusMessage.Should().Contain("denied").And.NotContain("secret diagnostic");
        vm.Confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task StopWaitingDoesNotPretendToRollbackAzure()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ProvisioningFake
        {
            WaitForCreate = async ct =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            },
        };
        using var vm = Create(service);
        await vm.LoadAsync();
        vm.Confirmed = true;
        var creating = vm.CreateAsync();
        await started.Task;

        vm.StopWaiting();
        (await creating).Should().BeFalse();

        vm.StatusMessage.Should().Contain("may still finish").And.Contain("nothing was deleted");
        vm.IsCreating.Should().BeFalse();
        vm.Confirmed.Should().BeFalse();
    }

    [Fact]
    public void FailedAzureOperationWithSuccessfulHttpStatusIsNotPresentedAsHttpSuccess()
    {
        AzureProvisioningViewModel.DescribeFailure(new RequestFailedException(200, "private body"))
            .Should().Contain("failed or cancelled provisioning").And.NotContain("private body");
    }

    [Fact]
    public async Task CreatedModelIsSelectedInTheCorrectRoleAfterRefresh()
    {
        var inventory = AzureSetupViewModelTests.ReadyInventory();
        var service = new ProvisioningFake();
        using var setup = new AzureSetupViewModel(inventory, new SetupCredentials(), new(), service);
        await setup.RefreshAsync();
        using var create = setup.CreateProvisioning(AzureModelRole.Transcription);
        await create.LoadAsync();
        create.Confirmed = true;
        await create.CreateAsync();
        var account = inventory.Accounts.Accounts[0];
        inventory.Accounts = new([account with { Deployments = [.. account.Deployments, create.CreatedDeployment!] }]);

        await setup.SelectProvisionedAsync(create, CancellationToken.None);

        setup.SelectedTranscription!.Deployment.Name.Should().Be(create.CreatedDeployment!.Name);
        setup.TranscriptionBackend.Should().Be("cloud");
    }

    [Theory]
    [InlineData(AzureModelRole.Chat, false)]
    [InlineData(AzureModelRole.Transcription, false)]
    [InlineData(AzureModelRole.Image, false)]
    [InlineData(AzureModelRole.Chat, true)]
    [InlineData(AzureModelRole.Transcription, true)]
    [InlineData(AzureModelRole.Image, true)]
    public async Task PartialRefreshFailureDoesNotMisreportSuccessfulAzureCreation(AzureModelRole role, bool globalFailure)
    {
        var inventory = AzureSetupViewModelTests.ReadyInventory();
        var service = new ProvisioningFake { CatalogRole = role };
        using var setup = new AzureSetupViewModel(inventory, new SetupCredentials(), new(), service);
        await setup.RefreshAsync();
        using var create = setup.CreateProvisioning(role);
        await create.LoadAsync();
        create.Confirmed = true;
        (await create.CreateAsync()).Should().BeTrue();
        var account = inventory.Accounts.Accounts[0] with
        {
            Deployments = [.. inventory.Accounts.Accounts[0].Deployments, create.CreatedDeployment!],
            FailureKind = globalFailure ? DiscoveryFailureKind.None : DiscoveryFailureKind.Network,
        };
        inventory.Accounts = new([account], globalFailure ? DiscoveryFailureKind.Network : DiscoveryFailureKind.None);

        await setup.SelectProvisionedAsync(create, CancellationToken.None);

        setup.StatusMessage.Should().Contain("Deployment succeeded").And.Contain("Refresh");
    }

    internal static AzureProvisioningViewModel Create(ProvisioningFake service, AzureModelRole? role = null)
    {
        var account = AzureSetupViewModelTests.Account("chat", [AzureSetupViewModelTests.Chat()]);
        return new(service, new SetupCredentials(), new("sub1", "Subscription"), [account], account, role);
    }
}

internal sealed class ProvisioningFake : IAzureProvisioningService
{
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public TokenCredential? Credential { get; private set; }
    public AzureDeploymentCreateRequest? Deployment { get; private set; }
    public Exception? Failure { get; init; }
    public bool NoModels { get; init; }
    public AzureModelRole CatalogRole { get; init; } = AzureModelRole.Transcription;
    public Func<CancellationToken, Task>? WaitForCreate { get; init; }

    public Task<AzureCreationContext> GetCreationContextAsync(TokenCredential credential, string subscriptionId, CancellationToken ct = default)
    {
        Reads++;
        return Task.FromResult(new AzureCreationContext(["rg"], [new("eastus", "East US")]));
    }
    public Task<AzureDeploymentCatalog> GetDeploymentCatalogAsync(TokenCredential credential, string accountId, CancellationToken ct = default)
    {
        Reads++;
        var sku = new AzureModelSkuInfo("GlobalStandard", 1, 10, 1, 1, [], "quota");
        return Task.FromResult(new AzureDeploymentCatalog(NoModels ? [] :
            [new("Microsoft", CatalogRole == AzureModelRole.Transcription ? "MAI-Transcribe-1" : "test-model", "1", CatalogRole, [sku])],
            [new("quota", 0, 10)], null));
    }
    public async Task<AzureAccountInfo> CreateResourceAsync(TokenCredential credential, AzureResourceCreateRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Writes++;
        Credential = credential;
        if (WaitForCreate is not null) await WaitForCreate(ct);
        if (Failure is not null) throw Failure;
        return AzureSetupViewModelTests.Account(request.Name, []);
    }
    public Task<AzureDeploymentInfo> DeployModelAsync(TokenCredential credential, AzureDeploymentCreateRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Writes++;
        Credential = credential;
        Deployment = request;
        return Task.FromResult(new AzureDeploymentInfo(request.Name, request.Model.Name, request.Model.Version,
            CatalogRole == AzureModelRole.Chat, CatalogRole == AzureModelRole.Transcription, CatalogRole == AzureModelRole.Image));
    }
}
