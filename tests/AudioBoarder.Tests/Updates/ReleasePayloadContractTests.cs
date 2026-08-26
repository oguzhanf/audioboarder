using System.Text.Json;
using AudioBoarder.App.Updates;

namespace AudioBoarder.Tests.Updates;

/// <summary>
/// Pins the shape of a real GitHub release payload against the updater's parser.
/// <para>
/// Every requirement here is silent when broken: the parser simply returns null and
/// no update is ever offered. A release published without asset digests, or with a
/// differently-named MSI, would leave installed users stranded with no error
/// anywhere — so the contract is asserted rather than assumed.
/// </para>
/// </summary>
public class ReleasePayloadContractTests
{
    /// <summary>Trimmed to the fields the parser reads, matching the live v0.7.0 payload.</summary>
    private const string LiveReleasePayload = """
        {
          "tag_name": "v0.7.0",
          "name": "AudioBoarder v0.7.0",
          "draft": false,
          "prerelease": false,
          "body": "## What changed\n\nThe board looks like a diagram now.",
          "assets": [
            {
              "name": "AudioBoarder-v0.7.0-win-x64-portable.zip",
              "size": 101227554,
              "digest": "sha256:d6298cdd3c7dcff31b58dcd5b45424e165f00ff8fe19235cb1760230087cb302",
              "browser_download_url": "https://github.com/oguzhanf/audioboarder/releases/download/v0.7.0/AudioBoarder-v0.7.0-win-x64-portable.zip"
            },
            {
              "name": "AudioBoarder-v0.7.0-win-x64.msi",
              "size": 78264260,
              "digest": "sha256:7aa3f575d75ad5eb89999a4496195e5059dc8bd7a7c8a878718a2ff097969e08",
              "browser_download_url": "https://github.com/oguzhanf/audioboarder/releases/download/v0.7.0/AudioBoarder-v0.7.0-win-x64.msi"
            },
            {
              "name": "SHA256SUMS.txt",
              "size": 206,
              "digest": "sha256:0939e8e9baf291749f0fd25347ec69f0c8d1429a333cf7d3f9f0f0740bafff06",
              "browser_download_url": "https://github.com/oguzhanf/audioboarder/releases/download/v0.7.0/SHA256SUMS.txt"
            }
          ]
        }
        """;

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void AnInstalledOlderBuildIsOfferedTheRelease()
    {
        var release = GitHubUpdateService.ParseRelease(Payload(LiveReleasePayload), new Version(0, 6, 1));

        release.Should().NotBeNull("an installed v0.6.1 must be offered v0.7.0");
        release!.TagName.Should().Be("v0.7.0");
        release.Version.Should().Be(new Version(0, 7, 0));
        // It must pick the MSI, not the portable zip or the checksum file.
        release.MsiUrl.AbsoluteUri.Should().EndWith("AudioBoarder-v0.7.0-win-x64.msi");
        release.Sha256.Should().Be("7aa3f575d75ad5eb89999a4496195e5059dc8bd7a7c8a878718a2ff097969e08");
        release.Size.Should().Be(78264260);
        release.ReleaseNotes.Should().NotBeEmpty("the notes are shown while the download runs");
    }

    [Fact]
    public void TheCurrentBuildIsNotOfferedAnUpdateToItself()
    {
        // A 3-part tag parses with Revision -1, so it must not compare greater than
        // the 4-part assembly version of the very build that is running.
        GitHubUpdateService.ParseRelease(Payload(LiveReleasePayload), new Version(0, 7, 0, 0))
            .Should().BeNull();
    }

    [Fact]
    public void AReleasePublishedWithoutAssetDigestsOffersNothing()
    {
        // GitHub only began returning `digest` recently; without it the updater has
        // no way to verify the download, so it must decline rather than trust it.
        var noDigest = LiveReleasePayload.Replace(
            "\"digest\": \"sha256:7aa3f575d75ad5eb89999a4496195e5059dc8bd7a7c8a878718a2ff097969e08\",",
            "");

        GitHubUpdateService.ParseRelease(Payload(noDigest), new Version(0, 6, 1))
            .Should().BeNull();
    }

    [Fact]
    public void AReleaseWithoutAWindowsMsiOffersNothing()
    {
        var renamed = LiveReleasePayload.Replace("-win-x64.msi", "-win-arm64.msi");

        GitHubUpdateService.ParseRelease(Payload(renamed), new Version(0, 6, 1))
            .Should().BeNull("the MSI naming convention is what the updater matches on");
    }

    [Fact]
    public void AnAssetHostedOffGitHubIsRejected()
    {
        var offsite = LiveReleasePayload.Replace(
            "https://github.com/oguzhanf/audioboarder/releases/download",
            "https://cdn.example.com/downloads");

        GitHubUpdateService.ParseRelease(Payload(offsite), new Version(0, 6, 1))
            .Should().BeNull("an installer must only ever come from the project's own releases");
    }
}
