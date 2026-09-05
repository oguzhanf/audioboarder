using System.Net;
using System.Text;
using System.Text.Json;
using AudioBoarder.Services.LLM;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.ResourceManager;
using Microsoft.Extensions.Logging;

namespace AudioBoarder.Tests.LLM;

public class AzureModelInventoryTests
{
    private static readonly string SubscriptionId = Guid.Empty.ToString();
    private static readonly string OtherSubscriptionId = new Guid(1, 0, 0, new byte[8]).ToString();
    private static readonly string AccountListPath =
        $"/subscriptions/{SubscriptionId}/providers/Microsoft.CognitiveServices/accounts";
    private const string PrivateDiagnostic = "PRIVATE Azure response with credentials";

    [Fact]
    public async Task ListsAllSubscriptionPagesUsingOnlyTheSuppliedCredential()
    {
        using var fixture = new InventoryFixture(
            Page([Subscription(SubscriptionId, "Development")],
                "https://management.azure.com/subscriptions?api-version=2022-12-01&$skiptoken=next"),
            Page([Subscription(OtherSubscriptionId, "Production")]));

        var result = await fixture.Inventory.ListSubscriptionsAsync(fixture.Credential);

        result.FailureKind.Should().Be(DiscoveryFailureKind.None);
        result.Message.Should().BeNull();
        result.Subscriptions.Should().Equal(
            new AzureSubscriptionInfo(SubscriptionId, "Development"),
            new AzureSubscriptionInfo(OtherSubscriptionId, "Production"));
        fixture.FactoryCredentials.Should().ContainSingle().Which.Should().BeSameAs(fixture.Credential);
        fixture.Credential.RequestCount.Should().BeGreaterThan(0);
        fixture.Requests.Select(r => r.Path).Should().Equal("/subscriptions", "/subscriptions");
        fixture.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task EmptySubscriptionsAreSuccessfulAndNotAnAuthenticationFailure()
    {
        using var fixture = new InventoryFixture(Page([]));

        var result = await fixture.Inventory.ListSubscriptionsAsync(fixture.Credential);

        result.Subscriptions.Should().BeEmpty();
        result.FailureKind.Should().Be(DiscoveryFailureKind.None);
        result.Message.Should().BeNull();
    }

    [Fact]
    public async Task ListsOpenAiAndAiServicesAcrossPagesWithoutQueryingOtherAccountKinds()
    {
        const string foundryEndpoint = "https://foundry.cognitiveservices.azure.com/";
        using var fixture = new InventoryFixture(
            Page([Account("openai", "openai"), Account("speech", "SpeechServices")],
                $"https://management.azure.com{AccountListPath}?api-version=2024-10-01&$skiptoken=next"),
            Page([]),
            Page([Account("foundry", "aIsErViCeS", foundryEndpoint), Account("face", "Face")]),
            Page([]));

        var result = await fixture.Inventory.ListAccountsAsync(fixture.Credential, SubscriptionId);

        result.FailureKind.Should().Be(DiscoveryFailureKind.None);
        result.Accounts.Select(a => a.Name).Should().Equal("openai", "foundry");
        var foundry = result.Accounts[1];
        foundry.Id.Should().Be(AccountId("foundry"));
        foundry.Kind.Should().Be("aIsErViCeS");
        foundry.Endpoint.Should().Be(foundryEndpoint);
        foundry.Region.Should().Be("eastus");
        fixture.Requests.Select(r => r.Path).Should().Equal(
            AccountListPath, $"{AccountId("openai")}/deployments",
            AccountListPath, $"{AccountId("foundry")}/deployments");
        fixture.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoMatchingAccountsAreAnActualSuccessfulEmptyInventory(bool hasOtherKind)
    {
        using var fixture = new InventoryFixture(Page(
            hasOtherKind ? [Account("speech", "SpeechServices")] : []));

        var result = await fixture.Inventory.ListAccountsAsync(fixture.Credential, SubscriptionId);

        result.Accounts.Should().BeEmpty();
        result.FailureKind.Should().Be(DiscoveryFailureKind.None);
        result.Message.Should().BeNull();
        fixture.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task DuplicateDeploymentNamesRemainScopedToTheirOwnResource()
    {
        using var fixture = new InventoryFixture(
            Page([Account("first", "OpenAI"), Account("second", "AIServices")]),
            Page([Deployment("first", "chat", "gpt-4o")]),
            Page([Deployment("second", "chat", "o4-mini")]));

        var result = await fixture.Inventory.ListAccountsAsync(fixture.Credential, SubscriptionId);

        result.Accounts.Should().HaveCount(2);
        result.Accounts[0].Deployments.Should().ContainSingle().Which.ModelName.Should().Be("gpt-4o");
        result.Accounts[1].Deployments.Should().ContainSingle().Which.ModelName.Should().Be("o4-mini");
        result.Accounts.SelectMany(a => a.Deployments).Select(d => d.Name).Should().Equal("chat", "chat");
        result.Accounts[0].Endpoint.Should().NotBe(result.Accounts[1].Endpoint);
    }

    [Theory]
    [InlineData(401, DiscoveryFailureKind.Authentication)]
    [InlineData(403, DiscoveryFailureKind.AccessDenied)]
    [InlineData(408, DiscoveryFailureKind.Service)]
    [InlineData(429, DiscoveryFailureKind.RateLimited)]
    [InlineData(503, DiscoveryFailureKind.Service)]
    [InlineData(400, DiscoveryFailureKind.Unknown)]
    public async Task ListingFailuresAreNotReportedAsSuccessfulEmptyInventories(
        int status, DiscoveryFailureKind expected)
    {
        using var fixture = new InventoryFixture(Failure(status), Failure(status));

        var subscriptions = await fixture.Inventory.ListSubscriptionsAsync(fixture.Credential);
        var accounts = await fixture.Inventory.ListAccountsAsync(fixture.Credential, SubscriptionId);

        subscriptions.Subscriptions.Should().BeEmpty();
        subscriptions.FailureKind.Should().Be(expected);
        subscriptions.Message.Should().NotBeNullOrWhiteSpace().And.NotContain(PrivateDiagnostic);
        accounts.Accounts.Should().BeEmpty();
        accounts.FailureKind.Should().Be(expected);
        accounts.Message.Should().NotBeNullOrWhiteSpace().And.NotContain(PrivateDiagnostic);
        fixture.Logger.Text.Should().NotContain(PrivateDiagnostic);
        fixture.Logger.Exceptions.Should().OnlyContain(e => e == null);
    }

    [Theory]
    [InlineData(401, DiscoveryFailureKind.Authentication)]
    [InlineData(403, DiscoveryFailureKind.AccessDenied)]
    [InlineData(429, DiscoveryFailureKind.RateLimited)]
    [InlineData(500, DiscoveryFailureKind.Service)]
    [InlineData(400, DiscoveryFailureKind.Unknown)]
    public async Task PerAccountFailureRetainsTheResourceAndDoesNotHideOtherDeployments(
        int status, DiscoveryFailureKind expected)
    {
        using var fixture = new InventoryFixture(
            Page([Account("denied", "OpenAI"), Account("empty", "AIServices"), Account("working", "OpenAI")]),
            Failure(status),
            Page([]),
            Page([Deployment("working", "chat", "gpt-5.6-sol")]));

        var result = await fixture.Inventory.ListAccountsAsync(fixture.Credential, SubscriptionId);

        result.FailureKind.Should().Be(DiscoveryFailureKind.None);
        result.Accounts.Should().HaveCount(3);
        var denied = result.Accounts[0];
        denied.Id.Should().Be(AccountId("denied"));
        denied.Endpoint.Should().Be("https://denied.openai.azure.com/");
        denied.Deployments.Should().BeEmpty();
        denied.FailureKind.Should().Be(expected);
        denied.Message.Should().NotBeNullOrWhiteSpace().And.NotContain(PrivateDiagnostic);
        var empty = result.Accounts[1];
        empty.Deployments.Should().BeEmpty();
        empty.FailureKind.Should().Be(DiscoveryFailureKind.None);
        empty.Message.Should().BeNull();
        result.Accounts[2].Deployments.Should().ContainSingle().Which.IsChat.Should().BeTrue();
        fixture.Logger.Text.Should().NotContain(PrivateDiagnostic);
        fixture.Logger.Exceptions.Should().OnlyContain(e => e == null);
    }

    [Theory]
    [InlineData("gpt-5.6-sol", true, false, false)]
    [InlineData("gpt-4o", true, false, false)]
    [InlineData("gpt-3.5-turbo", true, false, false)]
    [InlineData("o1-mini", true, false, false)]
    [InlineData("o3", true, false, false)]
    [InlineData("o4-mini", true, false, false)]
    [InlineData("DeepSeek-R1", true, false, false)]
    [InlineData("MAI-DS-R1", true, false, false)]
    [InlineData("MAI-1", true, false, false)]
    [InlineData("Phi-4", true, false, false)]
    [InlineData("gpt-transcribe", false, true, false)]
    [InlineData("gpt-4o-transcribe-diarize", false, true, false)]
    [InlineData("whisper", false, true, false)]
    [InlineData("mai-transcribe-1", false, true, false)]
    [InlineData("gpt-image-1", false, false, true)]
    [InlineData("MAI-Image-2.5", false, false, true)]
    [InlineData("dall-e-3", false, false, true)]
    [InlineData("gpt-4o-realtime-preview", false, false, false)]
    [InlineData("gpt-realtime", false, false, false)]
    [InlineData("gpt-live-transcribe", false, false, false)]
    [InlineData("gpt-realtime-whisper", false, false, false)]
    [InlineData("text-embedding-3-large", false, false, false)]
    [InlineData("gpt-embedding", false, false, false)]
    [InlineData("gpt-4o-audio-preview", false, false, false)]
    [InlineData("gpt-tts", false, false, false)]
    [InlineData("gpt-video", false, false, false)]
    [InlineData("gpt-moderation", false, false, false)]
    [InlineData("mai-voice-1", false, false, false)]
    [InlineData("unrecognized-model", false, false, false)]
    [InlineData(null, false, false, false)]
    public async Task ReportsSupportedRolesWithoutDroppingUnsupportedDeployments(
        string? model, bool chat, bool transcription, bool image)
    {
        using var fixture = new InventoryFixture(
            Page([Account("models", "AIServices")]),
            Page([Deployment("models", "gpt-chat-deployment-name", model)]));

        var result = await fixture.Inventory.ListAccountsAsync(fixture.Credential, SubscriptionId);

        var account = result.Accounts.Should().ContainSingle().Which;
        account.FailureKind.Should().Be(DiscoveryFailureKind.None);
        var deployment = account.Deployments.Should().ContainSingle().Which;
        deployment.Name.Should().Be("gpt-chat-deployment-name");
        deployment.ModelName.Should().Be(model ?? string.Empty);
        deployment.Version.Should().Be(model is null ? null : "2026-01-01");
        deployment.IsChat.Should().Be(chat);
        deployment.IsTranscription.Should().Be(transcription);
        deployment.IsImage.Should().Be(image);
        deployment.IsReady.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Succeeded", true)]
    [InlineData("succeeded", true)]
    [InlineData("Accepted", false)]
    [InlineData("Creating", false)]
    [InlineData("Updating", false)]
    [InlineData("Deleting", false)]
    [InlineData("Failed", false)]
    [InlineData("Canceled", false)]
    [InlineData("FutureUnrecognizedState", false)]
    public async Task KeepsNotReadyDeploymentsButDoesNotClaimTheyAreUsable(string? state, bool ready)
    {
        using var fixture = new InventoryFixture(
            Page([Account("models", "OpenAI")]),
            Page([Deployment("models", "chat", "o4-mini", state)]));

        var result = await fixture.Inventory.ListAccountsAsync(fixture.Credential, SubscriptionId);

        var deployment = result.Accounts.Single().Deployments.Should().ContainSingle().Which;
        deployment.IsChat.Should().BeTrue();
        deployment.IsReady.Should().Be(ready);
        if (!ready)
            deployment.DisplayName.Should().Contain("not ready");
    }

    [Fact]
    public async Task PaginationFailureRetainsKnownDeploymentsAndContinuesWithTheNextResource()
    {
        using var fixture = new InventoryFixture(
            Page([Account("partial", "OpenAI"), Account("next", "AIServices")]),
            Page([Deployment("partial", "chat", "gpt-4o")],
                $"https://management.azure.com{AccountId("partial")}/deployments?api-version=2024-10-01&$skiptoken=next"),
            Failure(403),
            Page([]));

        var result = await fixture.Inventory.ListAccountsAsync(fixture.Credential, SubscriptionId);

        result.FailureKind.Should().Be(DiscoveryFailureKind.None);
        result.Accounts.Should().HaveCount(2);
        result.Accounts[0].FailureKind.Should().Be(DiscoveryFailureKind.AccessDenied);
        result.Accounts[0].Deployments.Should().ContainSingle();
        result.Accounts[1].FailureKind.Should().Be(DiscoveryFailureKind.None);
    }

    [Theory]
    [InlineData("credential", DiscoveryFailureKind.Authentication)]
    [InlineData("authentication", DiscoveryFailureKind.Authentication)]
    [InlineData("network", DiscoveryFailureKind.Network)]
    [InlineData("transport", DiscoveryFailureKind.Network)]
    [InlineData("timeout", DiscoveryFailureKind.Service)]
    [InlineData("task-timeout", DiscoveryFailureKind.Service)]
    public async Task KnownNonHttpFailuresHaveSafeClassifications(string failure, DiscoveryFailureKind expected)
    {
        Exception exception = failure switch
        {
            "credential" => new CredentialUnavailableException(PrivateDiagnostic),
            "authentication" => new AuthenticationFailedException(PrivateDiagnostic),
            "network" => new HttpRequestException(PrivateDiagnostic),
            "transport" => new RequestFailedException(0, PrivateDiagnostic),
            "timeout" => new TimeoutException(PrivateDiagnostic),
            _ => new TaskCanceledException(PrivateDiagnostic),
        };
        var inventory = new AzureModelInventory(_ => throw exception);
        var credential = new TestCredential();

        var subscriptions = await inventory.ListSubscriptionsAsync(credential);
        var accounts = await inventory.ListAccountsAsync(credential, SubscriptionId);

        subscriptions.FailureKind.Should().Be(expected);
        subscriptions.Message.Should().NotBeNullOrWhiteSpace().And.NotContain(PrivateDiagnostic);
        accounts.FailureKind.Should().Be(expected);
        accounts.Message.Should().NotBeNullOrWhiteSpace().And.NotContain(PrivateDiagnostic);
    }

    [Fact]
    public async Task DoesNotCacheOrSubstituteTheCredentialBetweenCalls()
    {
        using var fixture = new InventoryFixture(Page([]), Page([]));
        var otherCredential = new TestCredential();

        await fixture.Inventory.ListSubscriptionsAsync(fixture.Credential);
        await fixture.Inventory.ListAccountsAsync(otherCredential, SubscriptionId);

        fixture.FactoryCredentials.Should().Equal(fixture.Credential, otherCredential);
        otherCredential.RequestCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task InvalidInputNeverCreatesAnArmClientOrFallsBackToSignIn()
    {
        using var fixture = new InventoryFixture();

        await FluentActions.Invoking(() => fixture.Inventory.ListSubscriptionsAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Invoking(() => fixture.Inventory.ListAccountsAsync(null!, SubscriptionId))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Invoking(() => fixture.Inventory.ListAccountsAsync(fixture.Credential, " "))
            .Should().ThrowAsync<ArgumentException>();

        fixture.FactoryCredentials.Should().BeEmpty();
        fixture.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AlreadyCanceledCallsNeverMakeAnArmRequest()
    {
        using var fixture = new InventoryFixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await FluentActions.Invoking(() => fixture.Inventory.ListSubscriptionsAsync(fixture.Credential, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Invoking(() => fixture.Inventory.ListAccountsAsync(fixture.Credential, SubscriptionId, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        fixture.FactoryCredentials.Should().BeEmpty();
        fixture.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("subscriptions")]
    [InlineData("accounts")]
    [InlineData("deployments")]
    public async Task CancellationDuringEnumerationIsNeverReportedAsAnAzureFailure(string stage)
    {
        using var cts = new CancellationTokenSource();
        using var fixture = new InventoryFixture();
        fixture.OnRequest = (request, token) =>
        {
            if (stage == "deployments" && request.RequestUri!.AbsolutePath == AccountListPath)
                return Page([Account("first", "OpenAI"), Account("not-requested", "AIServices")]);

            token.CanBeCanceled.Should().BeTrue();
            cts.Cancel();
            throw new TaskCanceledException("Canceled by the caller.", null, token);
        };

        Func<Task> action = stage == "subscriptions"
            ? () => fixture.Inventory.ListSubscriptionsAsync(fixture.Credential, cts.Token)
            : () => fixture.Inventory.ListAccountsAsync(fixture.Credential, SubscriptionId, cts.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        fixture.Requests.Should().HaveCount(stage == "deployments" ? 2 : 1);
        fixture.Logger.Text.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedProgrammingErrorsAreNotSwallowed(bool duringDeploymentListing)
    {
        using var fixture = new InventoryFixture();
        fixture.OnRequest = (request, _) =>
        {
            if (duringDeploymentListing && request.RequestUri!.AbsolutePath == AccountListPath)
                return Page([Account("first", "OpenAI")]);
            throw new InvalidOperationException("A programming error, not an Azure diagnostic.");
        };

        await FluentActions.Invoking(() =>
                fixture.Inventory.ListAccountsAsync(fixture.Credential, SubscriptionId))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void DisplayNamesIdentifyTheSelectionAndReadinessDefaultsToTrue()
    {
        new AzureSubscriptionInfo(SubscriptionId, "Development").DisplayName
            .Should().Contain("Development").And.Contain(SubscriptionId);
        new AzureSubscriptionInfo(SubscriptionId, "").DisplayName.Should().Be(SubscriptionId);
        new AzureAccountInfo(AccountId("resource"), "resource", "AIServices", "", "eastus", []).DisplayName
            .Should().Contain("resource").And.Contain("AIServices").And.Contain("eastus");
        var deployment = new AzureDeploymentInfo("chat", "gpt-4o", "version-1", true, false, false);
        deployment.DisplayName.Should().Contain("chat").And.Contain("gpt-4o").And.Contain("version-1");
        deployment.IsReady.Should().BeTrue();
    }

    private static string AccountId(string name) =>
        $"/subscriptions/{SubscriptionId}/resourceGroups/inventory-tests/providers/Microsoft.CognitiveServices/accounts/{name}";

    private static object Subscription(string id, string name) =>
        new { id = $"/subscriptions/{id}", subscriptionId = id, displayName = name, state = "Enabled" };

    private static object Account(string name, string kind, string? endpoint = null) =>
        new
        {
            id = AccountId(name),
            name,
            type = "Microsoft.CognitiveServices/accounts",
            kind,
            location = "eastus",
            properties = new { endpoint = endpoint ?? $"https://{name}.openai.azure.com/" },
        };

    private static object Deployment(string account, string name, string? model, string? state = "Succeeded") =>
        new
        {
            id = $"{AccountId(account)}/deployments/{name}",
            name,
            type = "Microsoft.CognitiveServices/accounts/deployments",
            properties = new
            {
                provisioningState = state,
                model = model is null ? null : new { name = model, format = "OpenAI", version = "2026-01-01" },
            },
        };

    private static HttpResponseMessage Page(object[] value, string? nextLink = null) =>
        JsonResponse(HttpStatusCode.OK, new { value, nextLink });

    private static HttpResponseMessage Failure(int status) =>
        JsonResponse((HttpStatusCode)status, new { error = new { code = "InventoryFailure", message = PrivateDiagnostic } });

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private sealed class InventoryFixture : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        private readonly HttpClient _client;

        public InventoryFixture(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
            _client = new HttpClient(this, disposeHandler: false);
            Inventory = new AzureModelInventory(credential =>
            {
                FactoryCredentials.Add(credential);
                var options = new ArmClientOptions
                {
                    Transport = new HttpClientTransport(_client),
                    Retry = { MaxRetries = 0 },
                };
                return new ArmClient(credential, defaultSubscriptionId: null, options);
            }, Logger);
        }

        public AzureModelInventory Inventory { get; }
        public TestCredential Credential { get; } = new();
        public InventoryLogger Logger { get; } = new();
        public List<TokenCredential> FactoryCredentials { get; } = [];
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? OnRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(OnRequest is not null
                ? OnRequest(request, cancellationToken)
                : _responses.Dequeue());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _client.Dispose();
                foreach (var response in _responses)
                    response.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class TestCredential : TokenCredential
    {
        public int RequestCount { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return new AccessToken("inventory-test-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class InventoryLogger : ILogger<AzureModelInventory>
    {
        private readonly List<string> _messages = [];
        public string Text => string.Join("\n", _messages);
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}
