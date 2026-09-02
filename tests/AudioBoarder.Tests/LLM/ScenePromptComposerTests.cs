using AudioBoarder.Core.LLM;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Transcript;
using AudioBoarder.Services.LLM;

namespace AudioBoarder.Tests.LLM;

public class ScenePromptComposerTests
{
    [Theory]
    [InlineData(DiagramIntent.SoftwareSystemArchitecture, "software-system")]
    [InlineData(DiagramIntent.SaaSMultiTenantArchitecture, "multi-tenant")]
    [InlineData(DiagramIntent.SecurityZeroTrustArchitecture, "zero-trust")]
    [InlineData(DiagramIntent.CloudNetworkArchitecture, "cloud network")]
    [InlineData(DiagramIntent.IntegrationDataFlowArchitecture, "data-flow")]
    [InlineData(DiagramIntent.DiscussionSummary, "discussion summary")]
    public void DeepAndContinuousPromptsRetainIntentDiscrimination(
        DiagramIntent intent,
        string expected)
    {
        var options = new AzureOpenAIOptions();
        var scene = new SceneGraph();
        var state = new DiagramIntentState(intent, DiagramIntentSelectionMode.Auto, .8, "test", 1);

        var deep = ScenePromptComposer.BuildSystemPrompt(options,
            new ScenePatchRequest(scene, [], DiagramIntent: intent, IntentState: state));
        var continuous = ScenePromptComposer.BuildSystemPrompt(options,
            new ScenePatchRequest(scene, [], Mode: GenerationMode.ContinuousExtraction,
                DiagramIntent: intent, IntentState: state));

        deep.ToLowerInvariant().Replace('-', ' ').Should().Contain(expected.Replace('-', ' '));
        continuous.ToLowerInvariant().Replace('-', ' ').Should().Contain(expected.Replace('-', ' '));
        continuous.Should().Contain("must not collapse architecture");
    }

    [Fact]
    public void UserPromptContainsIntentSceneIndexAndTranscriptDelta()
    {
        var scene = new SceneGraph();
        var segment = new TranscriptSegment(
            Guid.NewGuid(), TranscriptSpeaker.Remote, "new finalized fact",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(1));
        var request = new ScenePatchRequest(
            scene, [segment], DiagramIntent: DiagramIntent.SecurityZeroTrustArchitecture);

        var prompt = ScenePromptComposer.BuildUserPrompt(request);

        prompt.Should().Contain("Applied diagram intent: SecurityZeroTrustArchitecture");
        prompt.Should().Contain("Compact scene semantic index");
        prompt.Should().Contain("<transcript>");
        prompt.Should().Contain("new finalized fact");
    }
}
