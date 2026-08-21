using AudioBoarder.App.Configuration;

namespace AudioBoarder.Tests;

public class AudioBoarderSettingsTests
{
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
    public void Validate_RequiresSubscriptionWhenAutoDiscovering()
    {
        var s = new AudioBoarderSettings();
        s.AzureOpenAI.SubscriptionId = null;
        var problems = s.Validate();
        problems.Should().Contain(p => p.Contains("SubscriptionId"));
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
