using AudioBoarder.Services.Transcription.Cloud;

namespace AudioBoarder.Tests.Transcription;

/// <summary>
/// The transcriptions API "prompt" field biases recognition toward expected
/// vocabulary. Measured against a synthesised sample, an empty prompt left
/// gpt-transcribe hearing "per-view" for "Purview" and left both models
/// lower-casing product names; with the prompt both recovered every domain term.
/// These guard the default staying switched on.
/// </summary>
public class CloudTranscriptionPromptTests
{
    [Fact]
    public void VocabularyPromptIsOnByDefault()
    {
        new CloudTranscriptionOptions().Prompt
            .Should().Be(CloudTranscriptionOptions.DefaultVocabularyPrompt);
    }

    [Theory]
    [InlineData("Purview")]
    [InlineData("Fabric")]
    [InlineData("Power BI")]
    [InlineData("Copilot")]
    [InlineData("Entra")]
    [InlineData("Defender")]
    [InlineData("DLP")]
    public void DefaultPromptSeedsTheDomainTermsThatWereMisheard(string term)
    {
        CloudTranscriptionOptions.DefaultVocabularyPrompt.Should().Contain(term);
    }

    [Fact]
    public void DefaultPromptAsksForProductCapitalisation()
    {
        // Without this the models returned "purview" and "fabric" in lower case.
        CloudTranscriptionOptions.DefaultVocabularyPrompt
            .Should().Contain("capitalisation");
    }

    [Fact]
    public void ExplicitEmptyPromptDisablesBiasing()
    {
        // Empty string is a deliberate opt-out and must remain distinguishable
        // from "not configured", which falls back to the default.
        new CloudTranscriptionOptions { Prompt = "" }.Prompt.Should().BeEmpty();
    }
}
