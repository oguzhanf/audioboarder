using Azure.Core;
using AudioBoarder.Services.Transcription.Cloud;
using Microsoft.Extensions.Options;

namespace AudioBoarder.Tests.Transcription;

public sealed class AzureSpeechAuthorizationTests
{
    [Fact]
    public async Task SameConnectionReusesItsUnexpiredToken()
    {
        var credential = new CountingCredential("first");
        await using var service = new AzureSpeechStreamingService(Options.Create(new AzureSpeechSettings
        {
            Credential = credential, Region = "eastus2", ResourceId = "/accounts/speech",
        }));

        (await service.AcquireAadTokenAsync(default)).Should().Be("first");
        (await service.AcquireAadTokenAsync(default)).Should().Be("first");
        credential.Calls.Should().Be(1);
    }

    [Fact]
    public async Task NewSignedInIdentityDoesNotReusePreviousToken()
    {
        var first = new CountingCredential("first");
        var second = new CountingCredential("second");
        var settings = new AzureSpeechSettings { Credential = first, Region = "eastus2", ResourceId = "/accounts/speech" };
        await using var service = new AzureSpeechStreamingService(Options.Create(settings));
        await service.AcquireAadTokenAsync(default);

        settings.Credential = second;

        (await service.AcquireAadTokenAsync(default)).Should().Be("second");
        first.Calls.Should().Be(1);
        second.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("resource")]
    [InlineData("region")]
    public async Task ChangedConnectionRefreshesAuthorization(string changed)
    {
        var credential = new CountingCredential("synthetic");
        var settings = new AzureSpeechSettings
        {
            Credential = credential, TenantId = "tenant-a", Region = "eastus2", ResourceId = "/accounts/speech-a",
        };
        await using var service = new AzureSpeechStreamingService(Options.Create(settings));
        await service.AcquireAadTokenAsync(default);
        switch (changed)
        {
            case "tenant": settings.TenantId = "tenant-b"; break;
            case "resource": settings.ResourceId = "/accounts/speech-b"; break;
            case "region": settings.Region = "swedencentral"; break;
        }

        await service.AcquireAadTokenAsync(default);

        credential.Calls.Should().Be(2);
    }

    private sealed class CountingCredential(string value) : TokenCredential
    {
        public int Calls { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            return new AccessToken(value, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
