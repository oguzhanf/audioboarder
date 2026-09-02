using AudioBoarder.App.Updates;

namespace AudioBoarder.Tests.Updates;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("0.8.0-preview.1", "0.8.0-preview.2")]
    [InlineData("0.8.0-preview.2", "0.8.0")]
    [InlineData("0.8.0-alpha.9", "0.8.0-alpha.10")]
    [InlineData("0.8.0-1", "0.8.0-alpha")]
    [InlineData("0.8.0", "0.8.1")]
    [InlineData("0.8.9", "0.9.0")]
    [InlineData("0.9.9", "1.0.0")]
    public void OrdersVersionsAccordingToSemVer(string older, string newer)
    {
        SemanticVersion.Parse(newer).Should().BeGreaterThan(
            SemanticVersion.Parse(older));
    }

    [Theory]
    [InlineData("0.8.0", "0.8.0")]
    [InlineData("0.8.0+build.1", "0.8.0+build.2")]
    [InlineData("v0.8.0-preview.1+abc", "0.8.0-preview.1+xyz")]
    public void BuildMetadataDoesNotAffectPrecedence(string left, string right)
    {
        SemanticVersion.Parse(left).CompareTo(SemanticVersion.Parse(right))
            .Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.8")]
    [InlineData("0.8.0-preview.01")]
    [InlineData("vNext")]
    [InlineData("1.2.3.4")]
    public void RejectsMalformedVersions(string value)
    {
        SemanticVersion.TryParse(value, out _).Should().BeFalse();
    }

    [Fact]
    public void CurrentBuildReadsFullPackageVersionMetadata()
    {
        SemanticVersion.TryParse(ReleaseBuildMetadata.PackageVersion, out var current)
            .Should().BeTrue();
        current.Major.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void PreviewBuildIsOfferedCorrespondingStableRelease()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {
              "tag_name": "v0.8.0",
              "assets": [{
                "name": "AudioBoarder-v0.8.0-win-x64.msi",
                "browser_download_url": "https://github.com/oguzhanf/audioboarder/releases/download/v0.8.0/AudioBoarder-v0.8.0-win-x64.msi",
                "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "size": 123
              }]
            }
            """);

        GitHubUpdateService.ParseRelease(
                document.RootElement,
                SemanticVersion.Parse("0.8.0-preview.1"))
            .Should().NotBeNull();
    }

    [Fact]
    public void StableBuildIsNotOfferedSameStableRelease()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {
              "tag_name": "v0.8.0",
              "assets": [{
                "name": "AudioBoarder-v0.8.0-win-x64.msi",
                "browser_download_url": "https://github.com/oguzhanf/audioboarder/releases/download/v0.8.0/AudioBoarder-v0.8.0-win-x64.msi",
                "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "size": 123
              }]
            }
            """);

        GitHubUpdateService.ParseRelease(
                document.RootElement,
                SemanticVersion.Parse("0.8.0"))
            .Should().BeNull();
    }
}
