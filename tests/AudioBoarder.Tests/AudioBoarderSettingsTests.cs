using AudioBoarder.App.Configuration;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests;

public class AudioBoarderSettingsTests
{
    [Fact]
    public void InvalidRealtimeAndTranscriptionValuesAreRejected()
    {
        var settings = new AudioBoarderSettings();
        settings.Realtime.MinNewSegments = 0;
        settings.CloudTranscription.WindowSeconds = 0;
        settings.CloudTranscription.MaxBufferedSeconds = 181;

        settings.Validate().Should().Contain(
        [
            "Realtime.MinNewSegments must be positive",
            "CloudTranscription.WindowSeconds must be positive",
            "CloudTranscription.MaxBufferedSeconds must be between 0 and 180",
        ]);
    }
    [Fact]
    public void Defaults_AreProductionSafe()
    {
        var s = new AudioBoarderSettings();
        s.AzureOpenAI.UseManagedIdentity.Should().BeTrue();
        s.AzureOpenAI.AutoDiscover.Should().BeTrue();
        s.Whisper.AutoDownload.Should().BeTrue();
        s.Whisper.ModelSize.Should().Be("base");
        s.Audio.CaptureMicrophone.Should().BeTrue();
        s.Audio.CaptureLoopback.Should().BeTrue();
        s.Sessions.AutoSave.Should().BeTrue();
        s.Diagnostics.VerbosePayloadLogging.Should().BeFalse();
        s.Diagnostics.EnableLocalPerformanceTelemetry.Should().BeFalse();
        s.ImageGeneration.Enabled.Should().BeFalse();
        s.DiagramIntent.SelectionMode.Should().Be(DiagramIntentSelectionMode.Auto);
        s.DiagramIntent.PinnedIntent.Should().Be(DiagramIntent.SoftwareSystemArchitecture);
        s.Realtime.DeepPassIntervalSeconds.Should().Be(0);
        s.Realtime.DeepPauseSeconds.Should().BeInRange(20, 30);
    }

    [Fact]
    public void Validate_DetectsMissingAzureWhenAutoDiscoverOff()
    {
        var s = new AudioBoarderSettings();
        s.AzureOpenAI.AutoDiscover = false;
        var problems = s.Validate();
        problems.Should().Contain(p => p.Contains("Endpoint"));
        problems.Should().Contain(p => p.Contains("DeploymentName"));
    }

    [Fact]
    public void Validate_AcceptsAutoDiscoverWithSubscription()
    {
        var s = new AudioBoarderSettings();
        s.AzureOpenAI.SubscriptionId = "abc";
        var problems = s.Validate();
        problems.Should().BeEmpty();
    }

    [Fact]
    public void Validate_AllowsDefaultSubscriptionWhenAutoDiscovering()
    {
        var s = new AudioBoarderSettings();
        s.AzureOpenAI.SubscriptionId = null;
        var problems = s.Validate();
        problems.Should().BeEmpty(
            "FoundryDiscovery resolves the signed-in account's default subscription when none is pinned");
    }

    [Fact]
    public void Validate_RejectsNonPositiveTranscriptWindow()
    {
        var s = new AudioBoarderSettings { TranscriptWindow = TimeSpan.Zero };
        s.AzureOpenAI.SubscriptionId = "abc";
        var problems = s.Validate();
        problems.Should().Contain(p => p.Contains("TranscriptWindow"));
    }
}
