using System.Net;
using System.Text;
using System.Text.Json;
using AudioBoarder.Services.LLM;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;

namespace AudioBoarder.Tests.LLM;

public sealed class AzureProvisioningServiceTests
{
    private const string SubscriptionId = "00000000-0000-0000-0000-000000000000";
    private const string GroupName = "audio-tests";
    private const string AccountName = "audioboarder";
    private const string DeploymentName = "meeting-audio";
    private const string Region = "eastus";

    private static string GroupPath => $"/subscriptions/{SubscriptionId}/resourcegroups/{GroupName}";
    private static string AccountPath => $"{GroupPath}/providers/Microsoft.CognitiveServices/accounts/{AccountName}";
    private static string DeploymentPath => $"{AccountPath}/deployments/{DeploymentName}";
    private static bool SamePath(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task UnconfirmedCreateAndDeployNeverBuildAClientOrSendRequests()
    {
        using var fixture = new ProvisioningFixture();
        var credential = fixture.Credential;

        await FluentActions.Invoking(() => fixture.Service.CreateResourceAsync(credential,
                ValidCreateRequest(confirmed: false)))
            .Should().ThrowAsync<InvalidOperationException>();

        await FluentActions.Invoking(() => fixture.Service.DeployModelAsync(credential,
                ValidDeployRequest(confirmed: false, model: fixture.Model)))
            .Should().ThrowAsync<InvalidOperationException>();

        fixture.FactoryCalls.Should().Be(0);
        fixture.Requests.Should().BeEmpty();
        credential.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData(null, "audio-tests", "audioboarder", "OpenAI", Region)]
    [InlineData("not-a-guid", "audio-tests", "audioboarder", "OpenAI", Region)]
    [InlineData(SubscriptionId, "bad.", "audioboarder", "OpenAI", Region)]
    [InlineData(SubscriptionId, GroupName, "BadName", "OpenAI", Region)]
    [InlineData(SubscriptionId, GroupName, AccountName, "BadKind", Region)]
    [InlineData(SubscriptionId, GroupName, AccountName, "OpenAI", "east-us")]
    public async Task CreateValidationRejectsInvalidSubscriptionGroupNameKindAndRegion(
        string? subscriptionId, string resourceGroup, string name, string kind, string region)
    {
        using var fixture = new ProvisioningFixture();

        Func<Task> action = () => fixture.Service.CreateResourceAsync(
            fixture.Credential,
            new AzureResourceCreateRequest(subscriptionId!, resourceGroup, region, name, kind, false, false, true));

        await action.Should().ThrowAsync<ArgumentException>();
        fixture.FactoryCalls.Should().Be(0);
        fixture.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, typeof(ArgumentException))]
    [InlineData("bad", typeof(FormatException))]
    [InlineData("/providers/Microsoft.Storage/storageAccounts/foo", typeof(ArgumentException))]
    public async Task DeployValidationRejectsInvalidAccountResourceIds(string? accountId, Type expectedException)
    {
        using var fixture = new ProvisioningFixture();

        Func<Task> action = () => fixture.Service.DeployModelAsync(
            fixture.Credential,
            new AzureDeploymentCreateRequest(accountId!, DeploymentName, fixture.Model, "GlobalStandard", 1, true));

        await action.Should().ThrowAsync<Exception>()
            .Where(ex => expectedException.IsAssignableFrom(ex.GetType()));
        fixture.FactoryCalls.Should().Be(0);
        fixture.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DeploymentValidationRejectsNullModelBeforeAnyAzureCall()
    {
        using var fixture = new ProvisioningFixture();

        await FluentActions.Invoking(() => fixture.Service.DeployModelAsync(
                fixture.Credential,
                new AzureDeploymentCreateRequest(ValidAccountId(), DeploymentName, null!, "GlobalStandard", 1, true)))
            .Should().ThrowAsync<ArgumentNullException>();

        fixture.FactoryCalls.Should().Be(0);
        fixture.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("OpenAI", false, false)]
    [InlineData("AIServices", true, true)]
    public async Task CreateResourceUsesTheCallerCredentialAndExpectedPayload(
        string kind, bool createGroup, bool publicNetworkAccess)
    {
        using var fixture = new ProvisioningFixture
        {
            GroupExists = !createGroup,
            AccountExists = false,
            AccountCreateKind = kind,
            AccountCreateProvisioningState = "Succeeded",
        };

        var request = ValidCreateRequest(confirmed: true) with
        {
            Kind = kind,
            CreateResourceGroup = createGroup,
            PublicNetworkAccess = publicNetworkAccess,
        };

        var result = await fixture.Service.CreateResourceAsync(fixture.Credential, request);

        result.Name.Should().Be(AccountName);
        result.Kind.Should().Be(kind);
        fixture.FactoryCalls.Should().Be(1);
        fixture.FactoryCredentials.Should().ContainSingle().Which.Should().BeSameAs(fixture.Credential);
        fixture.Credential.RequestCount.Should().BeGreaterThan(0);

        fixture.Requests.Where(r => r.Method == HttpMethod.Put).Select(r => r.Header("If-None-Match"))
            .Should().OnlyContain(v => v == "*");

        var accountPut = fixture.Requests.Single(r => r.Method == HttpMethod.Put && SamePath(r.Path, AccountPath));
        using var body = JsonDocument.Parse(accountPut.Body);
        var root = body.RootElement;
        root.GetPropertyIgnoreCase("location").GetString().Should().Be(Region);
        root.GetPropertyIgnoreCase("kind").GetString().Should().Be(kind);
        root.GetPropertyIgnoreCase("sku").GetPropertyIgnoreCase("name").GetString().Should().Be("S0");
        var props = root.GetPropertyIgnoreCase("properties");
        props.GetPropertyIgnoreCase("customSubDomainName").GetString().Should().Be(AccountName);
        props.GetPropertyIgnoreCase("allowProjectManagement").GetBoolean().Should().Be(kind == "AIServices");
        props.GetPropertyIgnoreCase("disableLocalAuth").GetBoolean().Should().BeTrue();
        props.GetPropertyIgnoreCase("publicNetworkAccess").GetString()
            .Should().Be(publicNetworkAccess ? "Enabled" : "Disabled");
        if (kind == "AIServices")
            root.GetPropertyIgnoreCase("identity").GetPropertyIgnoreCase("type").GetString().Should().Be("SystemAssigned");
        else
            root.TryGetPropertyIgnoreCase("identity", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CreateResourceRequiresOptInForMissingGroupsAndPreservesExistingResources()
    {
        using var fixture = new ProvisioningFixture
        {
            GroupExists = false,
            AccountExists = false,
        };

        await FluentActions.Invoking(() => fixture.Service.CreateResourceAsync(fixture.Credential,
                ValidCreateRequest(confirmed: true) with { CreateResourceGroup = false }))
            .Should().ThrowAsync<InvalidOperationException>();

        fixture.FactoryCalls.Should().Be(1);
        fixture.Requests.Should().Contain(r => r.Method == HttpMethod.Get && SamePath(r.Path, AccountPath));
        fixture.Requests.Should().Contain(r => r.Method == HttpMethod.Get && SamePath(r.Path, GroupPath));
        fixture.Requests.Should().OnlyContain(r => r.Method != HttpMethod.Put);
    }

    [Fact]
    public async Task ExistingAccountNamesAreNeverOverwritten()
    {
        using var fixture = new ProvisioningFixture
        {
            GroupExists = true,
            AccountExists = true,
        };

        await FluentActions.Invoking(() => fixture.Service.CreateResourceAsync(fixture.Credential,
                ValidCreateRequest(confirmed: true)))
            .Should().ThrowAsync<InvalidOperationException>();

        fixture.Requests.Should().Contain(r => r.Method == HttpMethod.Get && SamePath(r.Path, AccountPath));
        fixture.Requests.Should().NotContain(r => r.Method == HttpMethod.Put && SamePath(r.Path, AccountPath));
    }

    [Fact]
    public async Task ExistingDeploymentNamesAreNeverOverwritten()
    {
        using var fixture = new ProvisioningFixture
        {
            AccountExists = true,
            DeploymentExists = true,
        };

        await FluentActions.Invoking(() => fixture.Service.DeployModelAsync(
                fixture.Credential,
                ValidDeployRequest(confirmed: true, model: fixture.Model)))
            .Should().ThrowAsync<InvalidOperationException>();

        fixture.Requests.Should().Contain(r => r.Method == HttpMethod.Get && SamePath(r.Path, DeploymentPath));
        fixture.Requests.Should().NotContain(r => r.Method == HttpMethod.Put && SamePath(r.Path, DeploymentPath));
    }

    [Fact]
    public async Task DeploymentCatalogFiltersUnsupportedDeprecatedAndProvisionedModels()
    {
        using var fixture = new ProvisioningFixture
        {
            AccountExists = true,
            CatalogModels = [
                CatalogModel("gpt-4o", "OpenAI", "2024-10-01",
                    skus: [
                        Sku("GlobalStandard", 1, 10, 1, 1, [1, 3, 5], "chat-quota"),
                        Sku("Provisioned", 1, 10, 1, 1, [], "ignored-provisioned")
                    ]),
                CatalogModel("gpt-transcribe", "OpenAI", "2026-01-01",
                    skus: [Sku("Developer", 2, 6, 2, 2, [], "audio-quota")]),
                CatalogModel("text-embedding-3-large", "OpenAI", "2024-01-01",
                    skus: [Sku("GlobalStandard", 1, 10, 1, 1, [], "ignored-role")]),
                CatalogModel("gpt-4o", "OpenAI", "2024-01-01", lifecycleStatus: "Deprecated",
                    skus: [Sku("GlobalStandard", 1, 10, 1, 1, [], "ignored-version")]),
            ],
            Quotas = [Quota("chat-quota", 2, 10), Quota("audio-quota", 1, 6)],
        };

        var catalog = await fixture.Service.GetDeploymentCatalogAsync(fixture.Credential, ValidAccountId());

        catalog.Models.Should().HaveCount(2);
        catalog.Models.Select(m => m.Name).Should().Equal("gpt-4o", "gpt-transcribe");

        var chat = catalog.Models[0];
        chat.Role.Should().Be(AzureModelRole.Chat);
        chat.Skus.Should().ContainSingle();
        chat.Skus[0].Name.Should().Be("GlobalStandard");
        chat.Skus[0].AllowedValues.Should().Equal(1, 3, 5);
        chat.Skus[0].Accepts(3).Should().BeTrue();
        chat.Skus[0].Accepts(2).Should().BeFalse();

        var transcribe = catalog.Models[1];
        transcribe.Role.Should().Be(AzureModelRole.Transcription);
        transcribe.Skus.Should().ContainSingle();
        transcribe.Skus[0].Name.Should().Be("Developer");
        transcribe.Skus[0].Step.Should().Be(2);
        transcribe.Skus[0].Accepts(4).Should().BeTrue();
        transcribe.Skus[0].Accepts(3).Should().BeFalse();
    }

    [Fact]
    public async Task DeploymentRejectsBadCapacityAndQuotaBeforeAnyWrite()
    {
        using var fixture = new ProvisioningFixture
        {
            AccountExists = true,
            CatalogModels = [
                CatalogModel("gpt-4o", "OpenAI", "2024-10-01",
                    skus: [Sku("GlobalStandard", 1, 10, 1, 1, [1, 3, 5], "chat-quota")])
            ],
            Quotas = [Quota("chat-quota", 9, 10)],
        };

        var catalog = await fixture.Service.GetDeploymentCatalogAsync(fixture.Credential, ValidAccountId());
        var model = catalog.Models.Single();

        await FluentActions.Invoking(() => fixture.Service.DeployModelAsync(fixture.Credential,
                ValidDeployRequest(confirmed: true, model: model) with { Capacity = 2 }))
            .Should().ThrowAsync<ArgumentException>();

        await FluentActions.Invoking(() => fixture.Service.DeployModelAsync(fixture.Credential,
                ValidDeployRequest(confirmed: true, model: model) with { Capacity = 3 }))
            .Should().ThrowAsync<InvalidOperationException>();

        fixture.Requests.Should().OnlyContain(r => r.Method != HttpMethod.Put);
    }

    [Fact]
    public async Task DeploymentUsesTheCurrentCatalogValuesInTheCreateBody()
    {
        using var fixture = new ProvisioningFixture
        {
            AccountExists = true,
            DeploymentExists = false,
            CatalogModels = [
                CatalogModel("gpt-4o", "OpenAI", "2024-10-01",
                    skus: [Sku("GlobalStandard", 1, 10, 1, 1, [1, 3, 5], "chat-quota")])
            ],
            Quotas = [Quota("chat-quota", 0, 10)],
            DeploymentCreateProvisioningState = "Succeeded",
        };

        var request = ValidDeployRequest(confirmed: true, model: fixture.Model) with
        {
            Capacity = 5,
            Sku = "GlobalStandard",
            Name = DeploymentName,
        };

        var deployment = await fixture.Service.DeployModelAsync(fixture.Credential, request);

        deployment.Name.Should().Be(DeploymentName);
        deployment.ModelName.Should().Be("gpt-4o");
        deployment.Version.Should().Be("2024-10-01");
        deployment.IsChat.Should().BeTrue();
        deployment.IsReady.Should().BeTrue();

        var deploymentPut = fixture.Requests.Single(r => r.Method == HttpMethod.Put && SamePath(r.Path, DeploymentPath));
        using var body = JsonDocument.Parse(deploymentPut.Body);
        var root = body.RootElement;
        root.GetPropertyIgnoreCase("sku").GetPropertyIgnoreCase("name").GetString().Should().Be("GlobalStandard");
        root.GetPropertyIgnoreCase("sku").GetPropertyIgnoreCase("capacity").GetInt32().Should().Be(5);
        var model = root.GetPropertyIgnoreCase("properties").GetPropertyIgnoreCase("model");
        model.GetPropertyIgnoreCase("format").GetString().Should().Be("OpenAI");
        model.GetPropertyIgnoreCase("name").GetString().Should().Be("gpt-4o");
        model.GetPropertyIgnoreCase("version").GetString().Should().Be("2024-10-01");
        fixture.Requests.Where(r => r.Method == HttpMethod.Put).Should().OnlyContain(r =>
            r.Header("If-None-Match") == "*");
    }

    [Fact]
    public async Task DeploymentRejectsStaleModelSelectionsAfterARefresh()
    {
        using var fixture = new ProvisioningFixture
        {
            AccountExists = true,
            DeploymentExists = false,
            CatalogModels = [
                CatalogModel("gpt-4o", "OpenAI", "2024-10-01",
                    skus: [Sku("GlobalStandard", 1, 10, 1, 1, [1, 3, 5], "chat-quota")])
            ],
            Quotas = [Quota("chat-quota", 0, 10)],
        };

        var staleModel = new AzureDeployableModel("OpenAI", "gpt-4o", "2023-01-01", AzureModelRole.Chat,
            [new AzureModelSkuInfo("GlobalStandard", 1, 10, 1, 1, [1, 3, 5], "chat-quota")]);

        await FluentActions.Invoking(() => fixture.Service.DeployModelAsync(fixture.Credential,
                ValidDeployRequest(confirmed: true, model: staleModel)))
            .Should().ThrowAsync<InvalidOperationException>();

        fixture.Requests.Should().NotContain(r => r.Method == HttpMethod.Put && SamePath(r.Path, DeploymentPath));
    }

    [Fact]
    public async Task DeploymentWaitsForSucceededLroAndTreatsFailedTerminalStatesAsFailures()
    {
        using var fixture = new ProvisioningFixture
        {
            AccountExists = true,
            DeploymentExists = false,
            CatalogModels = [
                CatalogModel("gpt-4o", "OpenAI", "2024-10-01",
                    skus: [Sku("GlobalStandard", 1, 10, 1, 1, [], "chat-quota")])
            ],
            Quotas = [Quota("chat-quota", 0, 10)],
            DeploymentCreateAsyncOperation = true,
            DeploymentCreatePollStatuses = ["Succeeded"],
            DeploymentCreateProvisioningState = "Succeeded",
        };

        await fixture.Service.DeployModelAsync(fixture.Credential,
            ValidDeployRequest(confirmed: true, model: fixture.Model));

        fixture.Requests.Should().Contain(r => r.Method == HttpMethod.Get && r.Path.Contains("operation", StringComparison.OrdinalIgnoreCase));
        fixture.Requests.Should().Contain(r => r.Method == HttpMethod.Get && SamePath(r.Path, DeploymentPath));

        using var failedFixture = new ProvisioningFixture
        {
            AccountExists = true,
            DeploymentExists = false,
            CatalogModels = [
                CatalogModel("gpt-4o", "OpenAI", "2024-10-01",
                    skus: [Sku("GlobalStandard", 1, 10, 1, 1, [], "chat-quota")])
            ],
            Quotas = [Quota("chat-quota", 0, 10)],
            DeploymentCreateAsyncOperation = false,
            DeploymentCreateProvisioningState = "Failed",
        };

        await FluentActions.Invoking(() => failedFixture.Service.DeployModelAsync(failedFixture.Credential,
                ValidDeployRequest(confirmed: true, model: failedFixture.Model)))
            .Should().ThrowAsync<RequestFailedException>();

        using var canceledFixture = new ProvisioningFixture
        {
            AccountExists = true,
            DeploymentExists = false,
            CatalogModels = [
                CatalogModel("gpt-4o", "OpenAI", "2024-10-01",
                    skus: [Sku("GlobalStandard", 1, 10, 1, 1, [], "chat-quota")])
            ],
            Quotas = [Quota("chat-quota", 0, 10)],
            DeploymentCreateAsyncOperation = false,
            DeploymentCreateProvisioningState = "Canceled",
        };
        using var cts = new CancellationTokenSource();

        await FluentActions.Invoking(() => canceledFixture.Service.DeployModelAsync(canceledFixture.Credential,
                ValidDeployRequest(confirmed: true, model: canceledFixture.Model), ct: cts.Token))
            .Should().ThrowAsync<RequestFailedException>();
    }

    [Fact]
    public async Task QuotaRead403ProducesExplicitWarningInsteadOfAFalseZero()
    {
        using var fixture = new ProvisioningFixture
        {
            AccountExists = true,
            CatalogModels = [
                CatalogModel("gpt-4o", "OpenAI", "2024-10-01",
                    skus: [Sku("GlobalStandard", 1, 10, 1, 1, [], "chat-quota")])
            ],
            QuotaFailureStatus = HttpStatusCode.Forbidden,
        };

        var catalog = await fixture.Service.GetDeploymentCatalogAsync(fixture.Credential, ValidAccountId());

        catalog.QuotaMessage.Should().NotBeNullOrWhiteSpace().And.Contain("quota");
        catalog.Quotas.Should().BeEmpty();
    }

    [Fact]
    public async Task AlreadyCancelledRequestsNeverBuildAClientOrSubmitAzureWrites()
    {
        using var fixture = new ProvisioningFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await FluentActions.Invoking(() => fixture.Service.CreateResourceAsync(fixture.Credential,
            ValidCreateRequest(true), ct: cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Invoking(() => fixture.Service.DeployModelAsync(fixture.Credential,
            ValidDeployRequest(true, fixture.Model), ct: cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();

        fixture.FactoryCalls.Should().Be(0);
        fixture.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CancellingLroPollingDoesNotResubmitOrRollBackTheDeployment()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new ProvisioningFixture
        {
            AccountExists = true,
            CatalogModels = [CatalogModel("gpt-4o", "OpenAI", "2024-10-01")],
            OnRequest = request =>
            {
                if (request.Path.Contains("/operations/", StringComparison.OrdinalIgnoreCase))
                    cancellation.Cancel();
            },
        };

        await FluentActions.Invoking(() => fixture.Service.DeployModelAsync(fixture.Credential,
            ValidDeployRequest(true, fixture.Model), ct: cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();

        fixture.Requests.Count(r => r.Method == HttpMethod.Put).Should().Be(1);
        fixture.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task CreateAndDeployPropagateAzurePermissionFailures()
    {
        using var createFixture = new ProvisioningFixture
        {
            GroupExists = true,
            AccountExists = false,
            AccountCreateFailureStatus = HttpStatusCode.Forbidden,
        };

        await FluentActions.Invoking(() => createFixture.Service.CreateResourceAsync(createFixture.Credential,
                ValidCreateRequest(confirmed: true)))
            .Should().ThrowAsync<RequestFailedException>();

        using var deployFixture = new ProvisioningFixture
        {
            AccountExists = true,
            DeploymentExists = false,
            CatalogModels = [
                CatalogModel("gpt-4o", "OpenAI", "2024-10-01",
                    skus: [Sku("GlobalStandard", 1, 10, 1, 1, [], "chat-quota")])
            ],
            Quotas = [Quota("chat-quota", 0, 10)],
            DeploymentCreateFailureStatus = HttpStatusCode.Forbidden,
        };

        await FluentActions.Invoking(() => deployFixture.Service.DeployModelAsync(deployFixture.Credential,
                ValidDeployRequest(confirmed: true, model: deployFixture.Model)))
            .Should().ThrowAsync<RequestFailedException>();
    }

    private static AzureResourceCreateRequest ValidCreateRequest(bool confirmed) =>
        new(SubscriptionId, GroupName, Region, AccountName, "OpenAI", true, false, confirmed);

    private static AzureDeploymentCreateRequest ValidDeployRequest(bool confirmed, AzureDeployableModel model) =>
        new(ValidAccountId(), DeploymentName, model, "GlobalStandard", 1, confirmed);

    private static string ValidAccountId() => AccountPath;

    private static ModelFixture CatalogModel(
        string name,
        string format,
        string version,
        IReadOnlyList<SkuFixture>? skus = null,
        string lifecycleStatus = "Active")
    {
        return new ModelFixture(name, format, version, lifecycleStatus, skus ?? [
            Sku("GlobalStandard", 1, 10, 1, 1, [], "chat-quota")
        ]);
    }

    private static SkuFixture Sku(
        string name, int minimum, int maximum, int step, int @default, IReadOnlyList<int> allowedValues, string usageName) =>
        new(name, minimum, maximum, step, @default, allowedValues, usageName);

    private static QuotaFixture Quota(string name, double current, double limit) => new(name, current, limit);

    private sealed record SkuFixture(
        string Name, int Minimum, int Maximum, int Step, int DefaultCapacity, IReadOnlyList<int> AllowedValues, string UsageName);

    private sealed record QuotaFixture(string Name, double Current, double Limit);

    private sealed class ProvisioningFixture : HttpMessageHandler
    {
        private readonly HttpClient _client;
        private readonly List<Route> _routes = [];

        public ProvisioningFixture()
        {
            _client = new HttpClient(this, disposeHandler: false);
            Service = new AzureProvisioningService((credential, options) =>
            {
                FactoryCalls++;
                FactoryCredentials.Add(credential);
                ObservedOptions.Add(options);
                options.Transport = new HttpClientTransport(_client);
                return new ArmClient(credential, defaultSubscriptionId: null, options);
            });
        }

        public AzureProvisioningService Service { get; }
        public TestCredential Credential { get; } = new();
        public List<TokenCredential> FactoryCredentials { get; } = [];
        public Action<CapturedRequest>? OnRequest { get; init; }
        public List<ArmClientOptions> ObservedOptions { get; } = [];
        public List<CapturedRequest> Requests { get; } = [];
        public int FactoryCalls { get; private set; }

        public bool GroupExists { get; init; } = true;
        public bool AccountExists { get; init; }
        public bool DeploymentExists { get; init; }
        public bool DeploymentCreateAsyncOperation { get; init; } = true;
        public string AccountCreateProvisioningState { get; init; } = "Succeeded";
        public string DeploymentCreateProvisioningState { get; init; } = "Succeeded";
        public string AccountCreateKind { get; init; } = "OpenAI";
        public HttpStatusCode? AccountCreateFailureStatus { get; init; }
        public HttpStatusCode? GroupCreateFailureStatus { get; init; }
        public HttpStatusCode? DeploymentCreateFailureStatus { get; init; }
        public HttpStatusCode? QuotaFailureStatus { get; init; }
        public List<ModelFixture> CatalogModels { get; init; } = [];
        public List<QuotaFixture> Quotas { get; init; } = [];
        public List<string> DeploymentCreatePollStatuses { get; init; } = ["Succeeded"];
        public AzureDeployableModel Model { get; } = new("OpenAI", "gpt-4o", "2024-10-01", AzureModelRole.Chat,
            [new AzureModelSkuInfo("GlobalStandard", 1, 10, 1, 1, [], "chat-quota")]);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = CapturedRequest.From(request);
            Requests.Add(captured);
            OnRequest?.Invoke(captured);
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var route in _routes)
            {
                if (route.TryHandle(captured, out var response))
                    return Task.FromResult(response!);
            }

            var responseFromBuiltins = HandleBuiltInRoute(captured);
            if (responseFromBuiltins is not null)
                return Task.FromResult(responseFromBuiltins);

            throw new InvalidOperationException($"No fake response for {captured.Method} {captured.Path}");
        }

        private HttpResponseMessage? HandleBuiltInRoute(CapturedRequest request)
        {
            if (SamePath(request.Path, GroupPath) && (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head))
            {
                _groupGetCount++;
                return JsonResponse(!GroupExists && _groupGetCount == 1 ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                    GroupResourceJson());
            }

            if (SamePath(request.Path, GroupPath) && request.Method == HttpMethod.Put)
            {
                if (GroupCreateFailureStatus is not null)
                    return JsonResponse(GroupCreateFailureStatus.Value, ErrorJson());
                return JsonResponse(HttpStatusCode.OK, GroupResourceJson());
            }

            if (SamePath(request.Path, AccountPath) && (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head))
            {
                _accountGetCount++;
                if (!AccountExists && _accountGetCount == 1)
                    return JsonResponse(HttpStatusCode.NotFound, ErrorJson());
                return JsonResponse(HttpStatusCode.OK, AccountResourceJson(AccountCreateProvisioningState));
            }

            if (SamePath(request.Path, AccountPath) && request.Method == HttpMethod.Put)
            {
                if (AccountCreateFailureStatus is not null)
                    return JsonResponse(AccountCreateFailureStatus.Value, ErrorJson());
                return CreateLongRunningCreateResponse(
                    AccountPath,
                    AccountResourceJson(AccountCreateProvisioningState),
                    AccountCreateProvisioningState,
                    $"https://management.azure.com/fake/operations/account-{Guid.NewGuid():N}");
            }

            if (SamePath(request.Path, $"{AccountPath}/models") && request.Method == HttpMethod.Get)
            {
                return JsonResponse(HttpStatusCode.OK, new { value = CatalogModels.Select(m => ModelJson(m)).ToArray() });
            }

            if (SamePath(request.Path, $"/subscriptions/{SubscriptionId}/providers/Microsoft.CognitiveServices/locations/{Region}/usages") &&
                request.Method == HttpMethod.Get)
            {
                if (QuotaFailureStatus is { } failure)
                    return JsonResponse(failure, ErrorJson());
                return JsonResponse(HttpStatusCode.OK, new
                {
                    value = Quotas.Select(q => new
                    {
                        name = new { value = q.Name },
                        currentValue = q.Current,
                        limit = q.Limit,
                    }).ToArray(),
                });
            }

            if (SamePath(request.Path, DeploymentPath) && (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head))
            {
                _deploymentGetCount++;
                return JsonResponse(!DeploymentExists && _deploymentGetCount == 1 ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                    DeploymentResourceJson(DeploymentCreateProvisioningState));
            }

            if (SamePath(request.Path, DeploymentPath) && request.Method == HttpMethod.Put)
            {
                if (DeploymentCreateFailureStatus is not null)
                    return JsonResponse(DeploymentCreateFailureStatus.Value, ErrorJson());
                return DeploymentCreateAsyncOperation
                    ? CreateLongRunningCreateResponse(
                        DeploymentPath,
                        DeploymentResourceJson(DeploymentCreateProvisioningState),
                        DeploymentCreateProvisioningState,
                        $"https://management.azure.com/fake/operations/deployment-{Guid.NewGuid():N}",
                        pollStatuses: DeploymentCreatePollStatuses)
                    : JsonResponse(HttpStatusCode.OK, DeploymentResourceJson(DeploymentCreateProvisioningState));
            }

            return null;
        }

        private HttpResponseMessage CreateLongRunningCreateResponse(
            string resourcePath,
            object body,
            string finalProvisioningState,
            string pollUrl,
            IReadOnlyList<string>? pollStatuses = null)
        {
            var response = JsonResponse(HttpStatusCode.Accepted, body);
            response.Headers.TryAddWithoutValidation("Azure-AsyncOperation", pollUrl);
            response.Headers.TryAddWithoutValidation("Location", $"https://management.azure.com{resourcePath}");
            response.Headers.TryAddWithoutValidation("Retry-After", "0");

            var statuses = pollStatuses ?? ["Succeeded"];
            _routes.Add(new Route(
                captured => captured.Method == HttpMethod.Get && SamePath(captured.Path, new Uri(pollUrl).AbsolutePath),
                statuses.Select(status => (Func<CapturedRequest, HttpResponseMessage>)(_ =>
                {
                    var poll = JsonResponse(HttpStatusCode.OK, new { status });
                    poll.Headers.TryAddWithoutValidation("Retry-After", "0");
                    return poll;
                })).ToArray()));

            _routes.Add(new Route(
                captured => SamePath(captured.Path, resourcePath) && captured.Method == HttpMethod.Get,
                _ => JsonResponse(HttpStatusCode.OK, finalProvisioningState == "Succeeded"
                    ? body
                    : ResourceWithState(resourcePath, finalProvisioningState))));

            response.StatusCode = HttpStatusCode.Created;
            return response;
        }

        private static object ResourceWithState(string path, string state) => new
        {
            id = path,
            name = path.Split('/').Last(),
            type = path.Contains("/deployments/", StringComparison.OrdinalIgnoreCase)
                ? "Microsoft.CognitiveServices/accounts/deployments"
                : "Microsoft.CognitiveServices/accounts",
            properties = new { provisioningState = state },
        };

        private object AccountResourceJson(string state) => new
        {
            id = AccountPath,
            name = AccountName,
            type = "Microsoft.CognitiveServices/accounts",
            kind = AccountCreateKind,
            location = Region,
            properties = new
            {
                endpoint = $"https://{AccountName}.openai.azure.com/",
                provisioningState = state,
            },
        };

        private object DeploymentResourceJson(string state) => new
        {
            id = DeploymentPath,
            name = DeploymentName,
            type = "Microsoft.CognitiveServices/accounts/deployments",
            properties = new
            {
                provisioningState = state,
                model = new { format = "OpenAI", name = Model.Name, version = Model.Version },
            },
            sku = new { name = "GlobalStandard", capacity = 1 },
        };

        private static object GroupResourceJson() => new
        {
            id = GroupPath,
            name = GroupName,
            location = Region,
            properties = new { provisioningState = "Succeeded" },
        };

        private static object ErrorJson() => new { error = new { code = "Denied", message = "private diagnostic" } };

        private static object ModelJson(ModelFixture model) => new
        {
            name = model.Name,
            format = model.Format,
            version = model.Version,
            lifecycleStatus = model.LifecycleStatus,
            skus = model.Skus.Select(s => new
            {
                name = s.Name,
                capacity = new
                {
                    minimum = s.Minimum,
                    maximum = s.Maximum,
                    step = s.Step,
                    @default = s.DefaultCapacity,
                    allowedValues = s.AllowedValues,
                },
                usageName = s.UsageName,
            }).ToArray(),
        };

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _client.Dispose();
            }

            base.Dispose(disposing);
        }

        private int _groupGetCount;
        private int _accountGetCount;
        private int _deploymentGetCount;
    }

    private sealed record ModelFixture(
        string Name, string Format, string Version, string LifecycleStatus, IReadOnlyList<SkuFixture> Skus);

    private sealed class Route
    {
        private readonly Queue<Func<CapturedRequest, HttpResponseMessage>> _responses;

        public Route(Predicate<CapturedRequest> match, params Func<CapturedRequest, HttpResponseMessage>[] responses)
        {
            Match = match;
            _responses = new Queue<Func<CapturedRequest, HttpResponseMessage>>(responses);
        }

        public Predicate<CapturedRequest> Match { get; }

        public bool TryHandle(CapturedRequest request, out HttpResponseMessage? response)
        {
            if (!Match(request) || _responses.Count == 0)
            {
                response = null;
                return false;
            }

            response = _responses.Dequeue()(request);
            return true;
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string Body, IReadOnlyDictionary<string, string[]> Headers)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

        public static CapturedRequest From(HttpRequestMessage request)
        {
            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
                headers[header.Key] = header.Value.ToArray();
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                    headers[header.Key] = header.Value.ToArray();
            }

            var body = request.Content is null ? string.Empty : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new CapturedRequest(request.Method, request.RequestUri!.AbsolutePath, body, headers);
        }
    }

    private sealed class TestCredential : TokenCredential
    {
        public int RequestCount { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return new AccessToken("caller-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        return response;
    }
}

internal static class AzureProvisioningTestJsonExtensions
{
    public static JsonElement GetPropertyIgnoreCase(this JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        throw new KeyNotFoundException(name);
    }

    public static bool TryGetPropertyIgnoreCase(this JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
