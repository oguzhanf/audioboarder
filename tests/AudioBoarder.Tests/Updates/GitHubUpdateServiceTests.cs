using System.Text.Json;
using System.Security.Cryptography;
using AudioBoarder.App.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioBoarder.Tests.Updates;

public sealed class GitHubUpdateServiceTests
{
    private const string AllowedSignerHash =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void ParseRelease_ReturnsNewerVerifiedMsi()
    {
        using var document = JsonDocument.Parse("""
            {
              "tag_name": "v0.7.0",
              "name": "AudioBoarder v0.7.0",
              "body": "Release notes",
              "assets": [{
                "name": "AudioBoarder-v0.7.0-win-x64.msi",
                "browser_download_url": "https://github.com/oguzhanf/audioboarder/releases/download/v0.7.0/AudioBoarder-v0.7.0-win-x64.msi",
                "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "size": 123
              }]
            }
            """);

        var release = GitHubUpdateService.ParseRelease(document.RootElement, new Version(0, 6, 1));

        Assert.NotNull(release);
        Assert.Equal(SemanticVersion.Parse("0.7.0"), release.Version);
        Assert.Equal(123, release.Size);
        Assert.Equal(new string('a', 64), release.Sha256);
    }

    [Theory]
    [InlineData("v0.6.1", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("v0.7.0", "")]
    [InlineData("v0.7.0", "sha256:short")]
    [InlineData("v0.7.0", "sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ParseRelease_RejectsCurrentOrUnverifiedMsi(string tag, string digest)
    {
        var json = $$"""
            {
              "tag_name": "{{tag}}",
              "assets": [{
                "name": "AudioBoarder-{{tag}}-win-x64.msi",
                "browser_download_url": "https://github.com/oguzhanf/audioboarder/releases/download/{{tag}}/AudioBoarder-{{tag}}-win-x64.msi",
                "digest": "{{digest}}",
                "size": 123
              }]
            }
            """;
        using var document = JsonDocument.Parse(json);

        Assert.Null(GitHubUpdateService.ParseRelease(
            document.RootElement, new Version(0, 6, 1)));
    }

    [Fact]
    public async Task DownloadAsync_VerifiesAndMovesCompletedMsi()
    {
        var bytes = "verified msi payload"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var tag = $"v99.0.{Guid.NewGuid():N}";
        var release = new UpdateRelease(
            SemanticVersion.Parse("99.0.0"),
            tag,
            "Test release",
            string.Empty,
            new Uri($"https://github.com/oguzhanf/audioboarder/releases/download/{tag}/AudioBoarder-{tag}-win-x64.msi"),
            hash,
            bytes.Length);
        using var http = new HttpClient(new StaticResponseHandler(bytes));
        var service = new GitHubUpdateService(
            http,
            NullLogger<GitHubUpdateService>.Instance,
            new Sha256FileVerifier(),
            new StubSignatureVerifier(isValid: true),
            AllowedSignerHash);
        var updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioBoarder", "updates", tag);

        try
        {
            var path = await service.DownloadAsync(release);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.False(File.Exists(path + ".download"));
        }
        finally
        {
            if (Directory.Exists(updateDirectory))
                Directory.Delete(updateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyUpdateAsync_FailsClosedWhenSignatureIsInvalid()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            $"audioboarder-update-{Guid.NewGuid():N}.msi");
        var bytes = "tamper-evident payload"u8.ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var service = new GitHubUpdateService(
                new HttpClient(new StaticResponseHandler(bytes)),
                NullLogger<GitHubUpdateService>.Instance,
                new Sha256FileVerifier(),
                new StubSignatureVerifier(isValid: false),
                AllowedSignerHash);

            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.False(await service.VerifyUpdateAsync(path, hash));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VerifyUpdateAsync_FailsClosedWhenInjectedHashVerifierRejects()
    {
        var signature = new StubSignatureVerifier(isValid: true);
        var service = new GitHubUpdateService(
            new HttpClient(new StaticResponseHandler(Array.Empty<byte>())),
            NullLogger<GitHubUpdateService>.Instance,
            new StubHashVerifier(isValid: false),
            signature,
            AllowedSignerHash);

        Assert.False(await service.VerifyUpdateAsync("not-read-by-stub.msi", new string('a', 64)));
        Assert.Equal(0, signature.CallCount);
    }

    [Fact]
    public async Task CheckAsync_PortableBuildNeverContactsReleaseEndpoint()
    {
        var handler = new CountingHandler();
        var service = new GitHubUpdateService(
            new HttpClient(handler),
            NullLogger<GitHubUpdateService>.Instance,
            new Sha256FileVerifier(),
            new StubSignatureVerifier(isValid: true),
            AllowedSignerHash,
            isPortableBuild: true);

        Assert.Null(await service.CheckAsync());
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task PreviewBuildQueriesReleaseCollectionAndDiscoversLaterPreview()
    {
        var handler = new JsonCaptureHandler("""
            [{
              "tag_name": "v0.8.0-preview.2",
              "draft": false,
              "prerelease": true,
              "assets": [{
                "name": "AudioBoarder-v0.8.0-preview.2-win-x64.msi",
                "browser_download_url": "https://github.com/oguzhanf/audioboarder/releases/download/v0.8.0-preview.2/AudioBoarder-v0.8.0-preview.2-win-x64.msi",
                "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "size": 123
              }]
            }]
            """);
        var service = new GitHubUpdateService(
            new HttpClient(handler),
            NullLogger<GitHubUpdateService>.Instance,
            new Sha256FileVerifier(),
            new StubSignatureVerifier(isValid: true),
            AllowedSignerHash,
            isPortableBuild: false,
            currentVersion: SemanticVersion.Parse("0.8.0-preview.1"),
            isMsiInstallation: () => true);

        var release = await service.CheckAsync();

        release.Should().NotBeNull();
        release!.Version.Should().Be(SemanticVersion.Parse("0.8.0-preview.2"));
        handler.RequestUri!.AbsoluteUri.Should().Contain("/releases?per_page=20");
    }

    [Fact]
    public async Task StableBuildUsesStableOnlyLatestEndpoint()
    {
        var handler = new JsonCaptureHandler("""
            {
              "tag_name": "v0.8.1",
              "draft": false,
              "prerelease": false,
              "assets": [{
                "name": "AudioBoarder-v0.8.1-win-x64.msi",
                "browser_download_url": "https://github.com/oguzhanf/audioboarder/releases/download/v0.8.1/AudioBoarder-v0.8.1-win-x64.msi",
                "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "size": 123
              }]
            }
            """);
        var service = new GitHubUpdateService(
            new HttpClient(handler),
            NullLogger<GitHubUpdateService>.Instance,
            new Sha256FileVerifier(),
            new StubSignatureVerifier(isValid: true),
            AllowedSignerHash,
            isPortableBuild: false,
            currentVersion: SemanticVersion.Parse("0.8.0"),
            isMsiInstallation: () => true);

        (await service.CheckAsync()).Should().NotBeNull();
        handler.RequestUri!.AbsoluteUri.Should().EndWith("/releases/latest");
    }

    [Fact]
    public void WindowsAuthenticodeVerifierRejectsUnsignedFile()
    {
        var result = new WindowsAuthenticodeVerifier().Verify(
            typeof(GitHubUpdateServiceTests).Assembly.Location,
            AllowedSignerHash);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.FailureReason!);
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public void SignerIdentity_AcceptsExactCertificateHashAllowlist(string value)
    {
        Assert.True(SignerIdentity.TryParseCertificateSha256Allowlist(value, out var hashes));
        Assert.Contains(new string('A', 64), hashes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CN=AudioBoarder")]
    [InlineData("aaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;not-a-hash")]
    public void SignerIdentity_RejectsMissingOrNonExactIdentity(string value)
    {
        Assert.False(SignerIdentity.TryParseCertificateSha256Allowlist(value, out var hashes));
        Assert.Empty(hashes);
    }

    [Fact]
    public async Task VerifyUpdateAsync_FailsClosedWhenSignerAllowlistIsAbsent()
    {
        var hash = new StubHashVerifier(isValid: true);
        var signature = new StubSignatureVerifier(isValid: true);
        var service = new GitHubUpdateService(
            new HttpClient(new StaticResponseHandler(Array.Empty<byte>())),
            NullLogger<GitHubUpdateService>.Instance,
            hash,
            signature,
            "");

        Assert.False(await service.VerifyUpdateAsync("not-read-by-stub.msi", new string('a', 64)));
        Assert.Equal(0, hash.CallCount);
        Assert.Equal(0, signature.CallCount);
    }

    private sealed class StaticResponseHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }

    }

    private sealed class JsonCaptureHandler(string json) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }

    private sealed class StubHashVerifier(bool isValid) : IFileHashVerifier
    {
        public int CallCount { get; private set; }

        public Task<bool> VerifySha256Async(
            string path,
            string expectedSha256,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(isValid);
        }
    }

    private sealed class StubSignatureVerifier(bool isValid) : IAuthenticodeVerifier
    {
        public int CallCount { get; private set; }

        public SignatureVerificationResult Verify(
            string path,
            string allowedSignerCertificateSha256)
        {
            CallCount++;
            return isValid
                ? SignatureVerificationResult.Valid(allowedSignerCertificateSha256)
                : SignatureVerificationResult.Invalid("test signature rejection");
        }
    }
}
