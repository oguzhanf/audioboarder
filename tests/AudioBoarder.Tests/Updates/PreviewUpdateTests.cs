using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AudioBoarder.App.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioBoarder.Tests.Updates;

public sealed class PreviewUpdateTests
{
    private static readonly SemanticVersion Current = SemanticVersion.Parse("0.8.0-preview.2");

    [Fact]
    public async Task MissingSignerDoesNotHideAnExplicitUnsignedPreviewOffer()
    {
        var release = Preview();
        using var http = new HttpClient(new ResponseHandler(JsonSerializer.SerializeToUtf8Bytes(new[] { Payload(release) })));
        var service = Service(http);

        var found = await service.CheckAsync(ignoreDeferrals: true);

        found.Should().NotBeNull();
        found!.IsUnsignedPreview.Should().BeTrue();
        found.RequiresManualInstaller.Should().BeFalse();
    }

    [Fact]
    public void NormalParserStillRejectsUnsignedInstallers()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Payload(Preview())));
        GitHubUpdateService.ParseRelease(json.RootElement, Current).Should().BeNull();
        GitHubUpdateService.ParseRelease(json.RootElement, Current, allowUnsignedPreviews: true)
            .Should().NotBeNull();
    }

    [Fact]
    public void StableInstallationsCannotOptIntoUnsignedPreviewParsing()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Payload(Preview())));
        GitHubUpdateService.ParseRelease(json.RootElement, SemanticVersion.Parse("0.8.0"), true)
            .Should().BeNull();
    }

    [Fact]
    public void UnsignedOfferRequiresExactOfficialRepositoryAndTagAssetUrl()
    {
        var release = Preview();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Payload(release with
        {
            MsiUrl = new Uri(release.MsiUrl.AbsoluteUri.Replace("oguzhanf/audioboarder", "other/repo")),
        })));
        GitHubUpdateService.ParseRelease(json.RootElement, Current, true).Should().BeNull();
    }

    [Fact]
    public void PreviewFlagMustAgreeWithTheReleaseTag()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Payload(Preview(), prerelease: false)));
        GitHubUpdateService.ParseRelease(json.RootElement, Current, true).Should().BeNull();
    }

    [Fact]
    public async Task StandardDownloadCannotInstallAnUnsignedOffer()
    {
        using var http = new HttpClient(new ResponseHandler([]));
        var service = Service(http);

        await FluentActions.Invoking(() => service.DownloadAsync(Preview()))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void PreviewApprovalIsRequiredAgainAtInstallTime()
    {
        using var http = new HttpClient(new ResponseHandler([]));
        var service = Service(http);

        service.Invoking(s => s.BeginInstallAndRestart("never-opened.msi", Preview()))
            .Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitPreviewDownloadStillEnforcesSha256(bool correctHash)
    {
        var bytes = "mock-preview-msi"u8.ToArray();
        var release = Preview() with
        {
            Sha256 = correctHash ? Convert.ToHexString(SHA256.HashData(bytes)) : new string('b', 64),
        };
        using var http = new HttpClient(new ResponseHandler(bytes));
        var signatures = new RejectSignatures();
        var service = Service(http, signatures);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioBoarder", "updates", release.TagName);
        try
        {
            if (correctHash)
            {
                var path = await service.DownloadApprovedPreviewAsync(release, userApproved: true);
                (await File.ReadAllBytesAsync(path)).Should().Equal(bytes);
            }
            else
            {
                await FluentActions.Invoking(() => service.DownloadApprovedPreviewAsync(release, true))
                    .Should().ThrowAsync<InvalidDataException>();
                Directory.EnumerateFiles(folder, "*.msi").Should().BeEmpty();
            }
            signatures.Calls.Should().Be(0, "hash-only verification is confined to the explicitly approved preview path");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void StableBuildAndWrongUrlCannotUsePreviewApproval()
    {
        using var http = new HttpClient(new ResponseHandler([]));
        var stable = Service(http, current: SemanticVersion.Parse("0.8.0"));
        stable.Invoking(s => s.ValidatePreviewApproval(Preview(), true)).Should().Throw<InvalidOperationException>();
        var preview = Service(http);
        preview.Invoking(s => s.ValidatePreviewApproval(Preview(), false)).Should().Throw<InvalidOperationException>();
        preview.Invoking(s => s.ValidatePreviewApproval(Preview() with
        {
            MsiUrl = new Uri("https://example.test/installer.msi"),
        }, true)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task ANewSignedReleaseWithoutEmbeddedTrustIsVisibleAsManualBootstrap()
    {
        var preview = Preview();
        var release = preview with
        {
            IsUnsignedPreview = false,
            MsiUrl = new Uri(preview.MsiUrl.AbsoluteUri.Replace("-unsigned.msi", ".msi")),
        };
        using var http = new HttpClient(new ResponseHandler(JsonSerializer.SerializeToUtf8Bytes(new[] { Payload(release) })));

        var found = await Service(http).CheckAsync(ignoreDeferrals: true);

        found.Should().NotBeNull();
        found!.RequiresManualInstaller.Should().BeTrue();
        found.IsUnsignedPreview.Should().BeFalse();
    }

    private static UpdateRelease Preview()
    {
        var version = SemanticVersion.Parse($"99.0.0-preview.{Random.Shared.Next(1000, int.MaxValue)}");
        var tag = $"v{version}";
        return new(version, tag, "Preview", "",
            new Uri($"https://github.com/oguzhanf/audioboarder/releases/download/{tag}/AudioBoarder-{tag}-win-x64-unsigned.msi"),
            new string('a', 64), 16, IsUnsignedPreview: true);
    }

    private static object Payload(UpdateRelease release, bool prerelease = true) => new
    {
        tag_name = release.TagName, draft = false, prerelease, name = "Preview",
        assets = new[] { new { name = Path.GetFileName(release.MsiUrl.LocalPath),
            browser_download_url = release.MsiUrl.AbsoluteUri, digest = $"sha256:{release.Sha256}", size = 16 } },
    };

    private static GitHubUpdateService Service(HttpClient http, RejectSignatures? signatures = null, SemanticVersion? current = null) =>
        new(http, NullLogger<GitHubUpdateService>.Instance, new Sha256FileVerifier(),
            signatures ?? new RejectSignatures(), "", currentVersion: current ?? Current,
            isMsiInstallation: () => true);

    private sealed class ResponseHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    private sealed class RejectSignatures : IAuthenticodeVerifier
    {
        public int Calls { get; private set; }
        public SignatureVerificationResult Verify(string path, string allowedSignerCertificateSha256)
        {
            Calls++;
            return SignatureVerificationResult.Invalid("Unsigned fixture");
        }
    }
}
