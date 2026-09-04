using System.Net;
using Azure;
using Azure.Core;
using Azure.Identity;
using AudioBoarder.App.Auth;
using AudioBoarder.App.Configuration;
using AudioBoarder.App.Health;
using AudioBoarder.App.ViewModels;
using AudioBoarder.Services.Imaging;
using AudioBoarder.Services.LLM;
using AudioBoarder.Services.Transcription.Cloud;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AudioBoarder.Tests.Health;

public class StartupHealthServiceTests
{
    [Fact]
    public void StartsInRestoringStateInsteadOfRedFailure()
    {
        var service = CreateService(new AudioBoarderSettings(), Credential(AzureCredentialState.Restoring));

        service.GetState(StartupHealthService.LlmKey).Should().BeEquivalentTo(
            new
            {
                Status = ComponentStatus.Checking,
                Action = HealthAction.None,
                Condition = HealthCondition.Restoring,
            });
    }

    [Fact]
    public async Task MissingAuthenticationRecordAndUnavailableAmbientCredentialMapsToSignIn()
    {
        var probe = new FakeTokenProbe(new CredentialUnavailableException(
            "no Azure CLI, managed identity, environment, or developer credential"));
        var service = CreateService(
            AutoDiscoverySettings(),
            Credential(AzureCredentialState.SignInRequired),
            probe);

        await service.RunLlmAsync();

        AssertState(service, ComponentStatus.ActionRequired, HealthAction.SignIn, HealthCondition.SignInRequired);
        service.GetState(StartupHealthService.LlmKey).Detail.Should()
            .Be("Sign in to Azure to discover deployments");
    }

    [Fact]
    public async Task MissingAuthenticationRecordCanUseValidAmbientCredential()
    {
        var discovery = new FakeDiscovery(SuccessfulDiscovery());
        var service = CreateService(
            AutoDiscoverySettings(),
            Credential(AzureCredentialState.SignInRequired),
            new FakeTokenProbe(),
            discovery);

        await service.RunLlmAsync();

        AssertState(service, ComponentStatus.Ready, HealthAction.None, HealthCondition.Ready);
        discovery.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task AuthenticationRequiredDuringTokenPreflightMapsToSignIn()
    {
        var probe = new FakeTokenProbe(new AuthenticationRequiredException(
            "expired",
            new TokenRequestContext(new[] { "https://management.azure.com/.default" })));
        var service = CreateService(
            AutoDiscoverySettings(),
            Credential(AzureCredentialState.SignedIn),
            probe);

        await service.RunLlmAsync();

        AssertState(service, ComponentStatus.ActionRequired, HealthAction.SignIn, HealthCondition.SignInRequired);
    }

    [Fact]
    public async Task AccessDeniedAfterValidAuthenticationShowsPermissionFailureNotSignIn()
    {
        var probe = new FakeTokenProbe(new RequestFailedException((int)HttpStatusCode.Forbidden, "denied"));
        var service = CreateService(
            AutoDiscoverySettings(),
            Credential(AzureCredentialState.SignedIn),
            probe);

        await service.RunLlmAsync();

        AssertState(service, ComponentStatus.Failed, HealthAction.None, HealthCondition.AccessDenied);
        service.GetState(StartupHealthService.LlmKey).Detail.Should().Contain("permission");
    }

    [Fact]
    public async Task NetworkFailureIsRetryable()
    {
        var service = CreateService(
            AutoDiscoverySettings(),
            Credential(AzureCredentialState.SignedIn),
            new FakeTokenProbe(new HttpRequestException("offline")));

        await service.RunLlmAsync();

        AssertState(service, ComponentStatus.Failed, HealthAction.Retry, HealthCondition.NetworkFailure);
    }

    [Fact]
    public async Task ServiceFailureIsRetryable()
    {
        var service = CreateService(
            AutoDiscoverySettings(),
            Credential(AzureCredentialState.SignedIn),
            new FakeTokenProbe(new RequestFailedException(
                (int)HttpStatusCode.ServiceUnavailable,
                "unavailable")));

        await service.RunLlmAsync();

        AssertState(service, ComponentStatus.Failed, HealthAction.Retry, HealthCondition.ServiceFailure);
    }

    [Fact]
    public async Task RateLimitHasExplicitRetryableState()
    {
        var service = CreateService(
            AutoDiscoverySettings(),
            Credential(AzureCredentialState.SignedIn),
            new FakeTokenProbe(new RequestFailedException(
                (int)HttpStatusCode.TooManyRequests,
                "throttled")));

        await service.RunLlmAsync();

        AssertState(service, ComponentStatus.RateLimited, HealthAction.Retry, HealthCondition.RateLimited);
    }

    [Fact]
    public async Task MissingManualConfigurationMapsToConfigureActionWithoutTokenProbe()
    {
        var settings = new AudioBoarderSettings();
        settings.AzureOpenAI.AutoDiscover = false;
        var probe = new FakeTokenProbe();
        var service = CreateService(settings, Credential(AzureCredentialState.SignInRequired), probe);

        await service.RunLlmAsync();

        AssertState(service, ComponentStatus.ActionRequired, HealthAction.Configure, HealthCondition.ConfigurationRequired);
        probe.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ValidTokenAndSuccessfulDiscoveryBecomeReady()
    {
        var discovery = new FakeDiscovery(SuccessfulDiscovery());
        var probe = new FakeTokenProbe();
        var service = CreateService(
            AutoDiscoverySettings(),
            Credential(AzureCredentialState.SignedIn),
            probe,
            discovery);

        await service.RunLlmAsync();

        AssertState(service, ComponentStatus.Ready, HealthAction.None, HealthCondition.Ready);
        probe.CallCount.Should().Be(1);
        discovery.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task PinnedCapabilityNamesStillTriggerEndpointResolution()
    {
        var settings = AutoDiscoverySettings();
        settings.AzureOpenAI.Endpoint = "https://chat.example/";
        settings.AzureOpenAI.DeploymentName = "chat-pin";
        settings.CloudTranscription.DeploymentName = "transcribe-pin";
        settings.ImageGeneration.Enabled = true;
        settings.ImageGeneration.DeploymentName = "image-pin";
        var cloudOptions = new CloudTranscriptionOptions
        {
            Endpoint = "https://chat.example/",
            DeploymentName = "transcribe-pin",
        };
        var imageOptions = new ImageGeneratorOptions
        {
            Endpoint = "https://chat.example/",
            DeploymentName = "image-pin",
        };
        var discovery = new FakeDiscovery(new DiscoveryResult(
            Success: true,
            Endpoint: "https://chat.example/",
            DeploymentName: "chat-pin",
            FallbackDeploymentName: null,
            ImageDeploymentName: "image-pin",
            ImageDeploymentIsMai: false,
            TranscribeDeploymentName: "transcribe-pin",
            TranscribeDeploymentIsMai: false,
            ImageEndpoint: "https://image-account.example/",
            TranscribeEndpoint: "https://transcribe-account.example/",
            AccountName: "chat-account",
            Region: "eastus",
            Message: "ready"));
        var service = CreateService(
            settings,
            Credential(AzureCredentialState.SignedIn),
            new FakeTokenProbe(),
            discovery,
            runtimeImageOptions: imageOptions,
            runtimeCloudOptions: cloudOptions);

        await service.RunLlmAsync();

        discovery.CallCount.Should().Be(1);
        imageOptions.Endpoint.Should().Be("https://image-account.example/");
        imageOptions.DeploymentName.Should().Be("image-pin");
        cloudOptions.Endpoint.Should().Be("https://transcribe-account.example/");
        cloudOptions.DeploymentName.Should().Be("transcribe-pin");
    }

    [Fact]
    public async Task ManuallyConfiguredEndpointStillRequiresAndInjectsVerifiedCredential()
    {
        var settings = new AudioBoarderSettings();
        settings.AzureOpenAI.AutoDiscover = false;
        settings.AzureOpenAI.Endpoint = "https://example.openai.azure.com/";
        settings.AzureOpenAI.DeploymentName = "gpt-test";
        var runtimeOptions = new AzureOpenAIOptions();
        var imageOptions = new ImageGeneratorOptions();
        var probe = new FakeTokenProbe();
        var service = CreateService(
            settings,
            Credential(AzureCredentialState.SignedIn),
            probe,
            runtimeOptions: runtimeOptions,
            runtimeImageOptions: imageOptions);

        await service.RunLlmAsync();

        AssertState(service, ComponentStatus.Ready, HealthAction.None, HealthCondition.Ready);
        probe.CallCount.Should().Be(1);
        runtimeOptions.Credential.Should().NotBeNull(
            "the exact credential that passed health must be used by diagram generation");
        imageOptions.Credential.Should().BeSameAs(runtimeOptions.Credential);
    }

    [Fact]
    public async Task ManuallyConfiguredEndpointShowsSignInWhenCredentialIsMissing()
    {
        var settings = new AudioBoarderSettings();
        settings.AzureOpenAI.AutoDiscover = false;
        settings.AzureOpenAI.Endpoint = "https://example.openai.azure.com/";
        settings.AzureOpenAI.DeploymentName = "gpt-test";
        var probe = new FakeTokenProbe(new CredentialUnavailableException(
            "no ambient credential"));
        var service = CreateService(
            settings,
            Credential(AzureCredentialState.SignInRequired),
            probe);

        await service.RunLlmAsync();

        AssertState(
            service,
            ComponentStatus.ActionRequired,
            HealthAction.SignIn,
            HealthCondition.SignInRequired);
        probe.CallCount.Should().Be(1);
    }

    [Theory]
    [InlineData(HealthAction.SignIn, true, false, false)]
    [InlineData(HealthAction.Configure, false, true, false)]
    [InlineData(HealthAction.Retry, false, false, true)]
    [InlineData(HealthAction.None, false, false, false)]
    public void ViewModelMapsExactlyOneContextualAzureAction(
        HealthAction action,
        bool signIn,
        bool configure,
        bool retry)
    {
        MainViewModel.MapHealthAction(action).Should().Be(
            new AzureHealthActionVisibility(signIn, configure, retry));
    }

    [Fact]
    public async Task SuccessfulInteractiveSignInMarksCheckingThenRerunsAllProbes()
    {
        var events = new List<string>();
        var credentials = new FakeCredentialProvider
        {
            SignInResult = (true, "Signed in."),
            Events = events,
        };
        var health = new FakeHealthRunner(events);
        var coordinator = new AzureSignInCoordinator(credentials, health);

        var result = await coordinator.SignInAndRefreshAsync(CancellationToken.None);

        result.Success.Should().BeTrue();
        events.Should().Equal("sign-in", "checking", "run-all");
    }

    private static AudioBoarderSettings AutoDiscoverySettings()
    {
        var settings = new AudioBoarderSettings();
        settings.AzureOpenAI.AutoDiscover = true;
        settings.AzureOpenAI.SubscriptionId = "00000000-0000-0000-0000-000000000000";
        return settings;
    }

    private static StartupHealthService CreateService(
        AudioBoarderSettings settings,
        IAzureCredentialProvider credentials,
        FakeTokenProbe? tokenProbe = null,
        IFoundryDiscovery? discovery = null,
        AzureOpenAIOptions? runtimeOptions = null,
        ImageGeneratorOptions? runtimeImageOptions = null,
        CloudTranscriptionOptions? runtimeCloudOptions = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<AzureOpenAIOptions>>(
            Options.Create(runtimeOptions ?? new AzureOpenAIOptions()));
        services.AddSingleton<IOptions<ImageGeneratorOptions>>(
            Options.Create(runtimeImageOptions ?? new ImageGeneratorOptions()));
        services.AddSingleton<IOptions<CloudTranscriptionOptions>>(
            Options.Create(runtimeCloudOptions ?? new CloudTranscriptionOptions()));
        return new StartupHealthService(
            services.BuildServiceProvider(),
            Options.Create(settings),
            credentials,
            tokenProbe ?? new FakeTokenProbe(),
            discovery ?? new FakeDiscovery(new DiscoveryResult(
                false, null, null, null, null, null, null, null, null, null, null, null,
                "not configured")));
    }

    private static FakeCredentialProvider Credential(AzureCredentialState state) =>
        new()
        {
            CurrentSnapshot = new AzureCredentialSnapshot(state, state == AzureCredentialState.SignedIn
                ? "user@example.com"
                : null),
        };

    private static DiscoveryResult SuccessfulDiscovery() => new(
        true,
        "https://example.openai.azure.com/",
        "gpt-5",
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        "account",
        "eastus",
        "ready");

    private static void AssertState(
        StartupHealthService service,
        ComponentStatus status,
        HealthAction action,
        HealthCondition condition)
    {
        var state = service.GetState(StartupHealthService.LlmKey);
        state.Status.Should().Be(status);
        state.Action.Should().Be(action);
        state.Condition.Should().Be(condition);
    }

    private sealed class FakeTokenProbe(Exception? exception = null) : IAzureManagementTokenProbe
    {
        public int CallCount { get; private set; }

        public Task ProbeAsync(TokenCredential credential, CancellationToken ct)
        {
            CallCount++;
            return exception is null ? Task.CompletedTask : Task.FromException(exception);
        }
    }

    private sealed class FakeDiscovery(DiscoveryResult result) : IFoundryDiscovery
    {
        public int CallCount { get; private set; }

        public Task<DiscoveryResult> DiscoverAsync(
            string? tenantId,
            string? subscriptionId,
            string? preferredDeploymentName = null,
            string? preferredRegion = null,
            string? preferredImageDeploymentName = null,
            string? preferredTranscribeDeploymentName = null,
            TokenCredential? credentialOverride = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeCredentialProvider : IAzureCredentialProvider
    {
        private readonly TokenCredential _credential = new StaticTokenCredential();

        public AzureCredentialSnapshot CurrentSnapshot { get; init; } =
            new(AzureCredentialState.SignedIn, "user@example.com");
        public (bool Success, string Message) SignInResult { get; init; } = (true, "Signed in.");
        public List<string>? Events { get; init; }

        public AzureCredentialSnapshot Snapshot => CurrentSnapshot;
        public string? SignedInAs => CurrentSnapshot.Username;
        public string? TenantId => null;
        public string? UserObjectId => null;
        public event EventHandler<AzureCredentialSnapshot>? StateChanged
        {
            add { }
            remove { }
        }

        public TokenCredential Get() => _credential;

        public bool TryGetSignedInCredential(out TokenCredential? credential)
        {
            credential = CurrentSnapshot.State == AzureCredentialState.SignedIn ? _credential : null;
            return credential is not null;
        }

        public Task<bool> TryRestoreAsync(CancellationToken ct) =>
            Task.FromResult(CurrentSnapshot.State == AzureCredentialState.SignedIn);

        public Task<(bool Success, string Message)> SignInInteractiveAsync(CancellationToken ct)
        {
            Events?.Add("sign-in");
            return Task.FromResult(SignInResult);
        }
    }

    private sealed class FakeHealthRunner(List<string> events) : IHealthProbeRunner
    {
        public void MarkLlmChecking(string detail, HealthCondition condition = HealthCondition.Unknown) =>
            events.Add("checking");

        public Task RunAllAsync(CancellationToken ct = default)
        {
            events.Add("run-all");
            return Task.CompletedTask;
        }
    }

    private sealed class StaticTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
