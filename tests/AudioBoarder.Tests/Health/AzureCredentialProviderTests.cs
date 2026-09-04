using Azure.Core;
using Azure.Identity;
using AudioBoarder.App.Auth;
using AudioBoarder.App.Configuration;
using Microsoft.Extensions.Options;

namespace AudioBoarder.Tests.Health;

public class AzureCredentialProviderTests
{
    [Fact]
    public void CreatingDefaultCredentialDoesNotClaimInteractiveSignIn()
    {
        var provider = CreateProvider(new FakeCredentialBackend());

        _ = provider.Get();

        provider.Snapshot.State.Should().Be(AzureCredentialState.Unknown);
        provider.TryGetSignedInCredential(out _).Should().BeFalse();
    }

    [Fact]
    public async Task MissingAuthenticationRecordDeterministicallyRequiresSignIn()
    {
        var backend = new FakeCredentialBackend { RestoreResult = null };
        var provider = CreateProvider(backend);

        var restored = await provider.TryRestoreAsync(CancellationToken.None);

        restored.Should().BeFalse();
        provider.Snapshot.State.Should().Be(AzureCredentialState.SignInRequired);
        provider.TryGetSignedInCredential(out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExpiredCachedTokenDeterministicallyRequiresSignIn()
    {
        var backend = new FakeCredentialBackend
        {
            RestoreException = new AuthenticationRequiredException(
                "expired",
                new TokenRequestContext(new[] { "https://management.azure.com/.default" })),
        };
        var provider = CreateProvider(backend);

        var restored = await provider.TryRestoreAsync(CancellationToken.None);

        restored.Should().BeFalse();
        provider.Snapshot.State.Should().Be(AzureCredentialState.SignInRequired);
        provider.Snapshot.FailureCategory.Should().Be("authentication_required");
    }

    [Fact]
    public async Task ValidCachedTokenPublishesSafeSignedInSnapshot()
    {
        var credential = new StaticTokenCredential();
        var backend = new FakeCredentialBackend
        {
            RestoreResult = new AzureCredentialSession(
                credential,
                "  user@example.com\r\n",
                "11111111-1111-1111-1111-111111111111.tenant"),
        };
        var provider = CreateProvider(backend);

        var restored = await provider.TryRestoreAsync(CancellationToken.None);

        restored.Should().BeTrue();
        provider.Snapshot.Should().Be(new AzureCredentialSnapshot(
            AzureCredentialState.SignedIn,
            "user@example.com"));
        provider.UserObjectId.Should().Be("11111111-1111-1111-1111-111111111111");
        provider.TryGetSignedInCredential(out var restoredCredential).Should().BeTrue();
        restoredCredential.Should().BeSameAs(credential);
    }

    private static AzureCredentialProvider CreateProvider(IAzureCredentialBackend backend) =>
        new(Options.Create(new AudioBoarderSettings()), backend);

    private sealed class FakeCredentialBackend : IAzureCredentialBackend
    {
        public AzureCredentialSession? RestoreResult { get; init; }
        public Exception? RestoreException { get; init; }

        public TokenCredential CreateDefaultCredential() => new StaticTokenCredential();

        public Task<AzureCredentialSession?> RestoreAsync(CancellationToken ct) =>
            RestoreException is null
                ? Task.FromResult(RestoreResult)
                : Task.FromException<AzureCredentialSession?>(RestoreException);

        public Task<AzureCredentialSession> SignInAsync(CancellationToken ct) =>
            Task.FromResult(RestoreResult ?? new AzureCredentialSession(
                new StaticTokenCredential(), "user@example.com", "oid.tenant"));
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
