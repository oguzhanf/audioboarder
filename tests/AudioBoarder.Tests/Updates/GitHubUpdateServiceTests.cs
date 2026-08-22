using System.Text.Json;
using System.Security.Cryptography;
using AudioBoarder.App.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioBoarder.Tests.Updates;

public sealed class GitHubUpdateServiceTests
{
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
        Assert.Equal(new Version(0, 7, 0), release.Version);
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
            new Version(99, 0),
            tag,
            "Test release",
            string.Empty,
            new Uri($"https://github.com/oguzhanf/audioboarder/releases/download/{tag}/AudioBoarder-{tag}-win-x64.msi"),
            hash,
            bytes.Length);
        using var http = new HttpClient(new StaticResponseHandler(bytes));
        var service = new GitHubUpdateService(
            http, NullLogger<GitHubUpdateService>.Instance);
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

    private sealed class StaticResponseHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
    }
}
