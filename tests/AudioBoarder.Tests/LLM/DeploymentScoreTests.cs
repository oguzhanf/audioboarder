using AudioBoarder.Services.LLM;

namespace AudioBoarder.Tests.LLM;

/// <summary>
/// Guards the deployment ranking. The original rule scored every gpt-5.x at a flat
/// 100, so a newer frontier deployment tied with an older one and the winner was
/// decided by region tie-break — which is how gpt-5.1 kept beating gpt-5.6-sol.
/// </summary>
public class DeploymentScoreTests
{
    [Theory]
    [InlineData("gpt-5.6-sol", "gpt-5.1")]
    [InlineData("gpt-5.6-sol", "gpt-5.4-pro")]
    [InlineData("gpt-5.6-luna", "gpt-5.1")]
    [InlineData("gpt-5.4", "gpt-5.1")]
    [InlineData("gpt-5.1", "gpt-4o")]
    [InlineData("gpt-4o", "gpt-3.5-turbo")]
    public void NewerOrHigherTierModelOutranksOlder(string better, string worse)
    {
        FoundryDiscovery.DeploymentScore(better)
            .Should().BeGreaterThan(FoundryDiscovery.DeploymentScore(worse),
                $"{better} should outrank {worse}");
    }

    [Fact]
    public void SolOutranksItsOwnFamilySiblings()
    {
        var sol = FoundryDiscovery.DeploymentScore("gpt-5.6-sol");
        FoundryDiscovery.DeploymentScore("gpt-5.6-terra").Should().BeLessThan(sol);
        FoundryDiscovery.DeploymentScore("gpt-5.6-luna").Should().BeLessThan(sol);
    }

    [Theory]
    [InlineData("gpt-5-6-sol", 5, 6)]   // Azure deployment names use dashes
    [InlineData("gpt-5.6-sol", 5, 6)]   // model names use dots
    [InlineData("gpt-5.1", 5, 1)]
    [InlineData("gpt-4o", 4, 0)]
    [InlineData("gpt-3.5-turbo", 3, 5)]
    [InlineData("whisper", 0, 0)]
    public void ParsesBothDottedAndDashedVersions(string name, int major, int minor)
    {
        FoundryDiscovery.ParseGptVersion(name).Should().Be((major, minor));
    }

    [Fact]
    public void DashedDeploymentNameRanksSameAsDottedModelName()
    {
        FoundryDiscovery.DeploymentScore("gpt-5-6-sol")
            .Should().Be(FoundryDiscovery.DeploymentScore("gpt-5.6-sol"));
    }

    [Theory]
    [InlineData("gpt-transcribe", "gpt-4o-transcribe")]
    [InlineData("gpt-transcribe", "gpt-4o-transcribe-diarize")]
    [InlineData("gpt-transcribe", "whisper")]
    [InlineData("gpt-4o-transcribe", "gpt-4o-mini-transcribe")]
    [InlineData("gpt-4o-transcribe-diarize", "gpt-4o-transcribe")]
    [InlineData("gpt-4o-transcribe", "whisper")]
    public void NewerTranscriptionModelOutranksOlder(string better, string worse)
    {
        FoundryDiscovery.TranscribeScore(better)
            .Should().BeGreaterThan(FoundryDiscovery.TranscribeScore(worse),
                $"{better} should outrank {worse}");
    }

    [Fact]
    public void PlainGptTranscribeIsNotMistakenForTheGpt4oFamily()
    {
        // "gpt-transcribe" previously fell through every gpt-4o-* check to the
        // catch-all and scored 10 — below whisper — so a newer deployment was ignored.
        FoundryDiscovery.TranscribeScore("gpt-transcribe")
            .Should().BeGreaterThan(FoundryDiscovery.TranscribeScore("whisper"));
    }

    [Theory]
    [InlineData("gpt-live-transcribe")]
    [InlineData("gpt-realtime-whisper")]
    public void RealtimeOnlyModelsAreNeverSelected(string model)
    {
        // These expose only a websocket transcription session, not
        // /audio/transcriptions, which is what the windowed pipeline posts to.
        FoundryDiscovery.IsRealtimeOnlyTranscribeModel(model).Should().BeTrue();
        FoundryDiscovery.TranscribeScore(model).Should().Be(0);
    }

    [Fact]
    public void BatchCapableModelsAreNotFlaggedRealtimeOnly()
    {
        FoundryDiscovery.IsRealtimeOnlyTranscribeModel("gpt-transcribe").Should().BeFalse();
        FoundryDiscovery.IsRealtimeOnlyTranscribeModel("gpt-4o-transcribe").Should().BeFalse();
        FoundryDiscovery.IsRealtimeOnlyTranscribeModel("whisper").Should().BeFalse();
    }
}
