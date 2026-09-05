using Azure.Core;
using AudioBoarder.App.Configuration;
using AudioBoarder.App.Setup;
using AudioBoarder.Services.LLM;

namespace AudioBoarder.Tests.Setup;

public sealed class AutomaticWorkspaceSetupTests
{
    [Fact]
    public async Task ExistingFastModelAndSpeechNeedNoResourceCreation()
    {
        var fixture = new Fixture();
        var setup = fixture.Create();
        var plan = await setup.InspectAsync(new SetupCredentials().Get(), new AudioBoarderSettings(), default);

        var result = await setup.ExecuteAsync(new SetupCredentials().Get(), "test-tenant", plan, true,
            new Progress<string>(), default);

        fixture.Writes.Should().Be(0);
        fixture.Probes.Should().Be(2);
        result.Models.TranscriptionBackend.Should().Be("speech");
        result.Models.Chat.ModelName.Should().Be("gpt-4.1-mini");
        var settings = new AudioBoarderSettings();
        result.ApplyTo(settings);
        settings.AzureSpeech.ResourceId.Should().Be(fixture.SpeechAccount.Id);
        settings.ModelAccounts.Single().SpeechResourceId.Should().Be(fixture.SpeechAccount.Id);
        settings.Realtime.MinIntervalSeconds.Should().Be(2);
    }

    [Fact]
    public async Task SetupNeverWritesWithoutExplicitApproval()
    {
        var fixture = new Fixture();
        var setup = fixture.Create();
        var plan = await setup.InspectAsync(new SetupCredentials().Get(), new(), default);
        await FluentActions.Invoking(() => setup.ExecuteAsync(new SetupCredentials().Get(), "tenant", plan,
            false, new Progress<string>(), default)).Should().ThrowAsync<InvalidOperationException>();
        fixture.Writes.Should().Be(0);
        fixture.Probes.Should().Be(0);
    }

    [Fact]
    public async Task ModelExistenceIsNotEnoughWhenTheStreamingConnectionFails()
    {
        var fixture = new Fixture { SpeechFailure = new HttpRequestException("network unavailable") };
        var setup = fixture.Create();
        var plan = await setup.InspectAsync(new SetupCredentials().Get(), new(), default);

        await FluentActions.Invoking(() => setup.ExecuteAsync(new SetupCredentials().Get(), "tenant", plan,
            true, new Progress<string>(), default)).Should().ThrowAsync<HttpRequestException>();

        fixture.Writes.Should().Be(0);
    }

    [Fact]
    public void ReasoningModelIsNotTheDefaultForLowLatencyExtraction()
    {
        AutomaticWorkspaceSetup.FastScore("gpt-4.1-mini").Should()
            .BeGreaterThan(AutomaticWorkspaceSetup.FastScore("gpt-5-mini"));
        AutomaticWorkspaceSetup.FastScore("gpt-5.6-sol").Should().Be(0);
    }

    [Theory]
    [InlineData("ready", true)]
    [InlineData("deleted-chat", false)]
    [InlineData("deleted-speech", false)]
    [InlineData("deleted-model", false)]
    [InlineData("explicit-local", true)]
    public async Task RestoredLoginChecksActualResourcesAndRespectsExplicitLocalMode(string scenario, bool ready)
    {
        var fixture = new Fixture();
        var setup = fixture.Create();
        var credential = new SetupCredentials().Get();
        var plan = await setup.InspectAsync(credential, new(), default);
        var result = await setup.ExecuteAsync(credential, "tenant", plan, true, new Progress<string>(), default);
        var settings = new AudioBoarderSettings();
        result.ApplyTo(settings);
        switch (scenario)
        {
            case "deleted-chat": fixture.ChatAvailable = false; break;
            case "deleted-speech": fixture.SpeechAvailable = false; break;
            case "deleted-model": settings.AzureOpenAI.DeploymentName = "deleted"; break;
            case "explicit-local":
                settings.CloudTranscription.Backend = "local";
                settings.AzureSpeech.ResourceId = null;
                fixture.SpeechAvailable = false;
                break;
        }

        (await setup.HasRequiredResourcesAsync(credential, settings, default)).Should().Be(ready);
        fixture.Writes.Should().Be(0);
    }

    private sealed class Fixture : IAzureModelInventory, IAzureProvisioningService, IAzureSpeechResources,
        IAzureWorkspaceAccess, IWorkspaceConnectionProbe
    {
        public AzureAccountInfo Account { get; } = AzureSetupViewModelTests.Account("ready",
            [new("live", "gpt-4.1-mini", "1", true, false, false)], Guid.Empty.ToString());
        public AzureAccountInfo SpeechAccount => Account with
        {
            Id = Account.Id + "-speech", Name = "speech", Kind = "SpeechServices", Deployments = [],
        };
        public int Writes { get; private set; }
        public int Probes { get; private set; }
        public Exception? SpeechFailure { get; init; }
        public bool ChatAvailable { get; set; } = true;
        public bool SpeechAvailable { get; set; } = true;
        public AutomaticWorkspaceSetup Create() => new(this, this, this, this, this);
        public Task<AzureSubscriptionInventory> ListSubscriptionsAsync(TokenCredential credential, CancellationToken ct = default) =>
            Task.FromResult(new AzureSubscriptionInventory([new(Guid.Empty.ToString(), "Test subscription")]));
        public Task<AzureAccountInventory> ListAccountsAsync(TokenCredential credential, string subscriptionId, CancellationToken ct = default) =>
            Task.FromResult(new AzureAccountInventory(ChatAvailable ? [Account] : []));
        public Task<AzureAccountInfo?> GetAccountAsync(TokenCredential credential, string resourceId, CancellationToken ct = default) =>
            Task.FromResult(resourceId == Account.Id ? (ChatAvailable ? Account : null) :
                resourceId == SpeechAccount.Id && SpeechAvailable ? SpeechAccount : null);
        public Task<IReadOnlyList<AzureAccountInfo>> ListSpeechResourcesAsync(TokenCredential credential, string subscriptionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AzureAccountInfo>>(SpeechAvailable ? [SpeechAccount] : []);
        public Task<AzureCreationContext> GetCreationContextAsync(TokenCredential credential, string subscriptionId, CancellationToken ct = default) =>
            throw new InvalidOperationException("Unexpected provisioning");
        public Task<AzureDeploymentCatalog> GetDeploymentCatalogAsync(TokenCredential credential, string accountId, CancellationToken ct = default) =>
            throw new InvalidOperationException("Unexpected deployment");
        public Task<AzureAccountInfo> CreateResourceAsync(TokenCredential credential, AzureResourceCreateRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
        { Writes++; return Task.FromResult(Account); }
        public Task<AzureDeploymentInfo> DeployModelAsync(TokenCredential credential, AzureDeploymentCreateRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
        { Writes++; return Task.FromResult(Account.Deployments[0]); }
        public Task EnsureOwnInferenceAccessAsync(TokenCredential credential, string accountId, bool speech, bool approved, CancellationToken ct)
        { Writes++; return Task.CompletedTask; }
        public Task SpeechAsync(TokenCredential credential, AzureAccountInfo resource, CancellationToken ct)
        { Probes++; return SpeechFailure is null ? Task.CompletedTask : Task.FromException(SpeechFailure); }
        public Task ChatAsync(TokenCredential credential, AzureAccountInfo account, AzureDeploymentInfo model, CancellationToken ct)
        { Probes++; return Task.CompletedTask; }
    }
}
