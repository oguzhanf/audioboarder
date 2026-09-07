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
    [InlineData(DiagramIntent.MeetingWhiteboard, "adaptive meeting whiteboard")]
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
        continuous.Should().Contain("must not collapse");
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

    [Theory]
    [InlineData(GenerationMode.ContinuousExtraction)]
    [InlineData(GenerationMode.DeepSynthesis)]
    public void DefaultPromptModelsIdeasInsteadOfForcingAnArchitecture(GenerationMode mode)
    {
        var request = new ScenePatchRequest(new SceneGraph(), [], Mode: mode);

        var prompt = ScenePromptComposer.BuildSystemPrompt(new AzureOpenAIOptions(), request);

        request.DiagramIntent.Should().Be(DiagramIntent.MeetingWhiteboard);
        prompt.Should().Contain("general-purpose");
        prompt.Should().Contain("concept cards");
        prompt.Should().Contain("separate cards with no edges");
        prompt.Should().Contain("Do not invent vendors");
        prompt.Should().NotContain("SOFTWARE SYSTEM ARCHITECTURE");
        prompt.Should().NotContain("Explicitly mentioned Microsoft products");
        prompt.Should().Contain(IconRegistry.PromptVocabulary);
    }

    [Fact]
    public void TechnicalVocabularyDoesNotSuggestUnmentionedAzureProducts()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new ScenePatchRequest(new SceneGraph(), [
            new(Guid.NewGuid(), TranscriptSpeaker.Local,
                "Kubernetes runs the web app. Redis is a cache. PostgreSQL is the database. A queue connects them.", now, now),
        ]);

        var prompt = ScenePromptComposer.BuildSystemPrompt(new AzureOpenAIOptions(), request);

        prompt.Should().NotContain("Explicitly mentioned Microsoft products");
        prompt.Should().NotContain("Azure App Service");
        prompt.Should().NotContain("Azure Kubernetes Service");
        prompt.Should().NotContain("Azure Managed Redis");
    }

    [Fact]
    public void ProductReferenceAppearsOnlyWhenNamedInTheCurrentDiscussion()
    {
        var now = DateTimeOffset.UtcNow;
        var scene = new SceneGraph();
        scene.TryAddUserNode(new SceneNode { Id = "old", Kind = NodeKind.Technology, Label = "Azure Kubernetes Service" });
        var request = new ScenePatchRequest(scene, [
            new(Guid.NewGuid(), TranscriptSpeaker.Local, "Azure App Service receives requests.", now, now),
        ]);

        var prompt = ScenePromptComposer.BuildSystemPrompt(new AzureOpenAIOptions(), request);

        prompt.Should().Contain("Azure App Service [");
        prompt.Should().NotContain("Azure Kubernetes Service [");
        prompt.Should().Contain("not a list to add");
    }

    [Fact]
    public void PriorConversationIsClearlySeparatedAndBounded()
    {
        var now = DateTimeOffset.UtcNow;
        var context = Enumerable.Range(0, 20).Select(index => new TranscriptSegment(
            Guid.NewGuid(), TranscriptSpeaker.Remote, $"{index:D2}-" + new string('x', 2000), now, now)).ToArray();
        var request = new ScenePatchRequest(new SceneGraph(), [
            new(Guid.NewGuid(), TranscriptSpeaker.Local, "That helps people share ideas.", now, now),
        ], Mode: GenerationMode.ContinuousExtraction, ConversationContext: context);

        var prompt = ScenePromptComposer.BuildUserPrompt(request);

        prompt.Should().Contain("<prior_context>");
        prompt.Should().Contain("Do not redraw or re-extract this older speech");
        prompt.Should().NotContain("00-");
        prompt.Should().Contain("19-");
        prompt.Should().NotContain(new string('x', 301));
        prompt.Should().Contain("That helps people share ideas.");
        prompt.Length.Should().BeLessThan(4000);
    }
}
