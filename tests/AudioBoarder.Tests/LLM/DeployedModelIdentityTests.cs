using AudioBoarder.Services.Imaging;
using AudioBoarder.Services.LLM;
using AudioBoarder.Services.Transcription.Cloud;

namespace AudioBoarder.Tests.LLM;

public sealed class DeployedModelIdentityTests
{
    [Fact]
    public void CustomChatAliasesUseTheActualModelForApiSelection()
    {
        var options = new AzureOpenAIOptions
        {
            Endpoint = "https://resource.example/",
            DeploymentName = "primary",
            FallbackDeploymentName = "fast",
            Model = new("https://resource.example/", "primary", "gpt-5.5"),
            FallbackModel = new("https://resource.example/", "fast", "gpt-4o-mini"),
        };
        options.GetModelName(false).Should().Be("gpt-5.5");
        options.GetModelName(true).Should().Be("gpt-4o-mini");
        AzureOpenAIScenePatchGenerator.IsReasoningModel(options.GetModelName(false)).Should().BeTrue();
    }

    [Fact]
    public void CustomMaiAliasesRouteToTheMaiClients()
    {
        var identity = new DeployedModelIdentity("https://resource.example/", "voice", "MAI-Transcribe-1");
        var transcription = new CloudTranscriptionOptions
        {
            Endpoint = identity.Endpoint, DeploymentName = identity.DeploymentName, Model = identity,
        };
        transcription.IsMaiModel.Should().BeTrue();
        var image = new ImageGeneratorOptions
        {
            Endpoint = identity.Endpoint, DeploymentName = "pictures",
            Model = new(identity.Endpoint, "pictures", "MAI-Image-2.5"),
        };
        image.EffectiveModelName.Should().Be("MAI-Image-2.5");
    }

    [Fact]
    public void ManualEndpointOrDeploymentEditsDoNotReuseStaleIdentity()
    {
        var identity = new DeployedModelIdentity("https://original.example/", "alias", "MAI-Transcribe-1");
        identity.Resolve("https://other.example/", "alias").Should().Be("alias");
        identity.Resolve("https://original.example/", "new-name").Should().Be("new-name");
    }
}
