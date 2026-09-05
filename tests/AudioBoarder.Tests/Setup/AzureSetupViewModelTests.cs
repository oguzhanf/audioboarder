using Azure.Core;
using AudioBoarder.App.Auth;
using AudioBoarder.App.Configuration;
using AudioBoarder.App.Setup;
using AudioBoarder.Services.LLM;

namespace AudioBoarder.Tests.Setup;

public sealed class AzureSetupViewModelTests
{
    [Fact]
    public async Task SignedOutUserIsAskedToSignInWithoutQueryingAzure()
    {
        var inventory = new SetupInventory();
        using var vm = new AzureSetupViewModel(inventory,
            new SetupCredentials { SignedIn = false }, new AudioBoarderSettings());

        await vm.RefreshAsync();

        inventory.SubscriptionCalls.Should().Be(0);
        vm.NeedsSetup.Should().BeTrue();
        vm.CanSave.Should().BeFalse();
        vm.StatusMessage.Should().Contain("Sign in");
    }

    [Fact]
    public async Task NoSubscriptionIsExplainedSeparatelyFromMissingResources()
    {
        var inventory = new SetupInventory { Subscriptions = new([]) };
        using var vm = Create(inventory);

        await vm.RefreshAsync();

        vm.StatusMessage.Should().Contain("No Azure subscriptions");
        vm.CanSave.Should().BeFalse();
        inventory.AccountCalls.Should().Be(0);
    }

    [Fact]
    public async Task MissingResourceProvidesSetupGuidance()
    {
        using var vm = Create(new SetupInventory());

        await vm.RefreshAsync();

        vm.NeedsSetup.Should().BeTrue();
        vm.StatusMessage.Should().Contain("No Azure OpenAI");
        vm.StatusMessage.Should().Contain("Create one");
        vm.CanProceed.Should().BeFalse();
    }

    [Fact]
    public async Task AccessDenialDoesNotClaimResourcesAreMissing()
    {
        var inventory = new SetupInventory
        {
            Accounts = new([], DiscoveryFailureKind.AccessDenied),
        };
        using var vm = Create(inventory);

        await vm.RefreshAsync();

        vm.StatusMessage.Should().Contain("denied permission");
        vm.StatusMessage.Should().Contain("does not mean they are missing");
        vm.NeedsSetup.Should().BeTrue();
    }

    [Fact]
    public async Task ResourceWithoutChatModelProvidesDeploymentGuidance()
    {
        using var vm = Create(new SetupInventory { Accounts = new([Account("empty", [])]) });

        await vm.RefreshAsync();

        vm.Accounts.Should().ContainSingle();
        vm.StatusMessage.Should().Contain("no ready, supported chat");
        vm.CanProceed.Should().BeFalse();
        vm.NeedsSetup.Should().BeTrue();
    }

    [Fact]
    public async Task ReadyChatWithLocalTranscriptionNeedsNoWizard()
    {
        using var vm = Create(ReadyInventory());

        await vm.RefreshAsync();

        vm.NeedsSetup.Should().BeFalse();
        vm.CanProceed.Should().BeTrue();
        vm.CanSave.Should().BeTrue();
    }

    [Fact]
    public async Task DeletedConfiguredChatIsNotSilentlyReplaced()
    {
        var settings = new AudioBoarderSettings();
        settings.AzureOpenAI.DeploymentName = "deleted-model";
        using var vm = Create(ReadyInventory(), settings);

        await vm.RefreshAsync();

        vm.NeedsSetup.Should().BeTrue();
        vm.SelectedChat!.Name.Should().Be("chat");
    }

    [Fact]
    public async Task DeletedConfiguredResourceIsNotSilentlyReplaced()
    {
        var settings = new AudioBoarderSettings();
        settings.AzureOpenAI.AccountResourceId = "/subscriptions/sub1/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/deleted";
        using var vm = Create(ReadyInventory(), settings);

        await vm.RefreshAsync();

        vm.NeedsSetup.Should().BeTrue();
    }

    [Fact]
    public async Task RequiredTranscriptionCanBeResolvedByChoosingLocal()
    {
        var settings = new AudioBoarderSettings();
        settings.CloudTranscription.Backend = "cloud";
        using var vm = Create(ReadyInventory(), settings);

        await vm.RefreshAsync();

        vm.NeedsSetup.Should().BeTrue();
        vm.CanSave.Should().BeFalse();
        vm.SelectionProblem.Should().Contain("transcription");
        vm.TranscriptionBackend = "local";
        vm.CanSave.Should().BeTrue();
    }

    [Fact]
    public async Task ImagesOnlyBecomeRequiredWhenEnabled()
    {
        using var vm = Create(ReadyInventory());
        await vm.RefreshAsync();

        vm.EnableImages = true;
        vm.CanSave.Should().BeFalse();
        vm.EnableImages = false;
        vm.CanSave.Should().BeTrue();
    }

    [Fact]
    public async Task UnreadyModelsAreNotSelectable()
    {
        var creating = new AzureDeploymentInfo("creating", "gpt-test", null, true, false, false, IsReady: false);
        using var vm = Create(new SetupInventory { Accounts = new([Account("chat", [creating])]) });

        await vm.RefreshAsync();

        vm.ChatDeployments.Should().BeEmpty();
        vm.CanSave.Should().BeFalse();
    }

    [Fact]
    public async Task PartialAccountAccessFailureRetainsAccessibleChoices()
    {
        var blocked = Account("blocked", []) with { FailureKind = DiscoveryFailureKind.AccessDenied };
        using var vm = Create(new SetupInventory { Accounts = new([blocked, Account("usable", [Chat()])]) });

        await vm.RefreshAsync();

        vm.SelectedAccount!.Name.Should().Be("usable");
        vm.CanSave.Should().BeTrue();
        vm.SelectedAccount = blocked;
        vm.CanSave.Should().BeFalse();
        vm.StatusMessage.Should().Contain("denied permission");
    }

    [Fact]
    public async Task FirstEmptySubscriptionDoesNotHideAnotherUsableSubscription()
    {
        var inventory = new SetupInventory
        {
            Subscriptions = new([new("sub1", "Empty"), new("sub2", "Usable")]),
            LoadAccounts = (id, _) => Task.FromResult(id == "sub1"
                ? new AzureAccountInventory([])
                : new AzureAccountInventory([Account("chat", [Chat()], "sub2")])),
        };
        using var vm = Create(inventory);

        await vm.RefreshAsync();

        vm.SelectedSubscription!.Id.Should().Be("sub2");
        vm.NeedsSetup.Should().BeFalse();
    }

    [Fact]
    public async Task MissingSavedSubscriptionRequiresExplicitSelection()
    {
        var settings = new AudioBoarderSettings();
        settings.AzureOpenAI.SubscriptionId = "retired-sub";
        using var vm = Create(ReadyInventory(), settings);

        await vm.RefreshAsync();

        vm.SelectedSubscription.Should().BeNull();
        vm.StatusMessage.Should().Contain("saved subscription is not visible");
        vm.CanSave.Should().BeFalse();
    }

    [Fact]
    public async Task DifferentTenantDoesNotUseTheOldLoginForDiscovery()
    {
        var settings = new AudioBoarderSettings();
        settings.AzureOpenAI.TenantId = "new-tenant";
        var inventory = ReadyInventory();
        using var vm = Create(inventory, settings);

        await vm.RefreshAsync();

        inventory.SubscriptionCalls.Should().Be(0);
        vm.CanSave.Should().BeFalse();
        vm.StatusMessage.Should().Contain("restart first");
    }

    [Fact]
    public async Task DuplicateDeploymentNamesKeepTheirOwnEndpoints()
    {
        var audio = new AzureDeploymentInfo("shared-name", "gpt-4o-transcribe", null, false, true, false);
        var inventory = new SetupInventory
        {
            Accounts = new([Account("chat", [Chat(), Chat("fast")]),
                Account("audio-a", [audio]), Account("audio-b", [audio])]),
        };
        using var vm = Create(inventory);
        await vm.RefreshAsync();
        vm.SelectedTranscription = vm.TranscriptionDeployments.Single(d => d.Account.Name == "audio-b");
        vm.SelectedFastChat = vm.ChatDeployments.Single(d => d.Name == "fast");

        var selection = vm.GetSelection();
        var settings = new AudioBoarderSettings();
        selection.ApplyTo(settings);

        settings.AzureOpenAI.Endpoint.Should().Be("https://chat.openai.azure.com/");
        settings.CloudTranscription.Endpoint.Should().Be("https://audio-b.openai.azure.com/");
        settings.CloudTranscription.DeploymentName.Should().Be("shared-name");
        settings.AzureOpenAI.FallbackDeploymentName.Should().Be("fast");
        settings.AzureOpenAI.AutoDiscover.Should().BeFalse();
        settings.ModelAccounts.Single().TranscriptionEndpoint.Should().Be(settings.CloudTranscription.Endpoint);
    }

    [Fact]
    public async Task ChangingResourcesClearsStalePrimaryAndFastModels()
    {
        var inventory = new SetupInventory
        {
            Accounts = new([Account("a", [Chat(), Chat("fast")]), Account("b", [Chat("other")])]),
        };
        using var vm = Create(inventory);
        await vm.RefreshAsync();
        vm.SelectedFastChat = vm.ChatDeployments.Single(d => d.Name == "fast");

        vm.SelectedAccount = vm.Accounts.Single(a => a.Name == "b");

        vm.SelectedChat!.Name.Should().Be("other");
        vm.SelectedFastChat.Should().BeNull();
    }

    [Fact]
    public async Task CancelledOrStaleRefreshCannotReplaceNewerInventory()
    {
        var delayed = new TaskCompletionSource<AzureSubscriptionInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var inventory = ReadyInventory();
        inventory.LoadSubscriptions = _ => ++calls == 1
            ? delayed.Task : Task.FromResult(new AzureSubscriptionInventory([new("sub1", "Current")]));
        using var vm = Create(inventory);
        var oldLoad = vm.RefreshAsync();
        await vm.RefreshAsync();
        delayed.SetResult(new AzureSubscriptionInventory([]));
        await oldLoad;

        vm.Subscriptions.Single().Name.Should().Be("Current");
        vm.CanSave.Should().BeTrue();
        vm.IsBusy.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("audio")]
    public async Task ManualCloudConfigurationMustMatchItsEffectiveEndpoint(string? deployment)
    {
        var settings = new AudioBoarderSettings();
        settings.AzureOpenAI.AutoDiscover = false;
        settings.AzureOpenAI.Endpoint = "https://chat.openai.azure.com/";
        settings.AzureOpenAI.DeploymentName = "chat";
        settings.CloudTranscription.Backend = "cloud";
        settings.CloudTranscription.DeploymentName = deployment;
        var audio = new AzureDeploymentInfo("audio", "gpt-4o-transcribe", null, false, true, false);
        using var vm = Create(new SetupInventory
        {
            Accounts = new([Account("chat", [Chat()]), Account("other", [audio])]),
        }, settings);

        await vm.RefreshAsync();

        vm.NeedsSetup.Should().BeTrue("a visible deployment on a different endpoint does not configure the running service");
    }

    [Fact]
    public async Task RefreshPreservesDraftModelAndExplicitNoneTranscription()
    {
        var audio = new AzureDeploymentInfo("audio", "gpt-4o-transcribe", null, false, true, false);
        using var vm = Create(new SetupInventory
        {
            Accounts = new([Account("chat", [Chat(), Chat("second"), audio])]),
        });
        await vm.RefreshAsync();
        vm.SelectedChat = vm.ChatDeployments.Single(d => d.Name == "second");
        vm.SelectedTranscription = null;

        await vm.RefreshAsync();

        vm.SelectedChat!.Name.Should().Be("second");
        vm.SelectedTranscription.Should().BeNull();
    }

    [Fact]
    public async Task RemovedDraftModelRequiresAnotherExplicitChoice()
    {
        var inventory = new SetupInventory
        {
            Accounts = new([Account("chat", [Chat(), Chat("second")])]),
        };
        using var vm = Create(inventory);
        await vm.RefreshAsync();
        vm.SelectedChat = vm.ChatDeployments.Single(d => d.Name == "second");
        inventory.Accounts = new([Account("chat", [Chat()])]);

        await vm.RefreshAsync();

        vm.SelectedChat.Should().BeNull();
        vm.CanSave.Should().BeFalse();
    }

    [Fact]
    public async Task CompletingLoginAfterDialogClosesDoesNotRestartDiscovery()
    {
        var signIn = new TaskCompletionSource<(bool Success, string Message)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inventory = new SetupInventory();
        using var cancellation = new CancellationTokenSource();
        using var vm = new AzureSetupViewModel(inventory,
            new SetupCredentials { SignIn = _ => signIn.Task }, new AudioBoarderSettings());
        var login = vm.SignInAsync(cancellation.Token);
        cancellation.Cancel();
        vm.Dispose();
        signIn.SetResult((true, "signed in"));

        await login;

        inventory.SubscriptionCalls.Should().Be(0);
    }

    internal static AzureDeploymentInfo Chat(string name = "chat") => new(name, "gpt-test", null, true, false, false);
    internal static AzureAccountInfo Account(string name, IReadOnlyList<AzureDeploymentInfo> deployments, string subscription = "sub1") =>
        new($"/subscriptions/{subscription}/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/{name}",
            name, "AIServices", $"https://{name}.openai.azure.com/", "eastus", deployments);
    internal static SetupInventory ReadyInventory() => new() { Accounts = new([Account("chat", [Chat()])]) };
    private static AzureSetupViewModel Create(SetupInventory inventory, AudioBoarderSettings? settings = null) =>
        new(inventory, new SetupCredentials(), settings ?? new AudioBoarderSettings());
}

internal sealed class SetupInventory : IAzureModelInventory
{
    public AzureSubscriptionInventory Subscriptions { get; set; } = new([new("sub1", "Test")]);
    public AzureAccountInventory Accounts { get; set; } = new([]);
    public Func<string, CancellationToken, Task<AzureAccountInventory>>? LoadAccounts { get; init; }
    public Func<CancellationToken, Task<AzureSubscriptionInventory>>? LoadSubscriptions { get; set; }
    public int SubscriptionCalls { get; private set; }
    public int AccountCalls { get; private set; }

    public Task<AzureSubscriptionInventory> ListSubscriptionsAsync(TokenCredential credential, CancellationToken ct = default)
    {
        SubscriptionCalls++;
        return LoadSubscriptions?.Invoke(ct) ?? Task.FromResult(Subscriptions);
    }

    public Task<AzureAccountInventory> ListAccountsAsync(TokenCredential credential, string subscriptionId, CancellationToken ct = default)
    {
        AccountCalls++;
        return LoadAccounts?.Invoke(subscriptionId, ct) ?? Task.FromResult(Accounts);
    }
}

internal sealed class SetupCredentials : IAzureCredentialProvider
{
    public bool SignedIn { get; init; } = true;
    public string? SignedInAs => SignedIn ? "test@example.com" : null;
    public string? TenantId => "test-tenant";
    public string? UserObjectId => null;
    public Func<CancellationToken, Task<(bool Success, string Message)>>? SignIn { get; init; }
    public AzureCredentialSnapshot Snapshot => new(SignedIn ? AzureCredentialState.SignedIn : AzureCredentialState.SignInRequired, SignedInAs);
    public event EventHandler<AzureCredentialSnapshot>? StateChanged { add { } remove { } }
    public TokenCredential Get() => new TestCredential();
    public bool TryGetSignedInCredential(out TokenCredential? credential)
    {
        credential = SignedIn ? Get() : null;
        return SignedIn;
    }
    public Task<bool> TryRestoreAsync(CancellationToken ct) => Task.FromResult(SignedIn);
    public Task<(bool Success, string Message)> SignInInteractiveAsync(CancellationToken ct) =>
        SignIn?.Invoke(ct) ?? Task.FromResult((SignedIn, SignedIn ? "Signed in." : "Cancelled."));

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unit tests must not request Azure tokens.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unit tests must not request Azure tokens.");
    }
}
