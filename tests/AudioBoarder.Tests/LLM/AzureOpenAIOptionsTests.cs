using AudioBoarder.Services.LLM;

namespace AudioBoarder.Tests.LLM;

public class AzureOpenAIOptionsTests
{
    [Fact]
    public void IsConfigured_RequiresEndpointAndDeployment()
    {
        var o = new AzureOpenAIOptions();
        o.IsConfigured.Should().BeFalse();

        o.Endpoint = "https://x.cognitiveservices.azure.com/";
        o.IsConfigured.Should().BeFalse();

        o.DeploymentName = "gpt-5.4-pro";
        o.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void SystemPrompt_HasReasonableDefault()
    {
        var o = new AzureOpenAIOptions();
        o.SystemPrompt.Should().NotBeNullOrWhiteSpace();
        o.SystemPrompt.Should().Contain("ScenePatch");
    }
}

public class FoundryDiscoveryShapeTests
{
    [Fact]
    public void DiscoveryResult_RecordWorks()
    {
        var r = new DiscoveryResult(
            Success: true,
            Endpoint: "https://x/",
            DeploymentName: "gpt-5.4-pro",
            FallbackDeploymentName: "gpt-5.3-chat",
            ImageDeploymentName: "gpt-image-2",
            ImageDeploymentIsMai: false,
            TranscribeDeploymentName: "gpt-4o-transcribe",
            TranscribeDeploymentIsMai: false,
            ImageEndpoint: "https://image-host/",
            TranscribeEndpoint: "https://transcribe-host/",
            AccountName: "contoso-ai-resource",
            Region: "eastus2",
            Message: "ok");
        r.Success.Should().BeTrue();
        r.Endpoint.Should().Be("https://x/");
        r.DeploymentName.Should().Be("gpt-5.4-pro");
        r.FallbackDeploymentName.Should().Be("gpt-5.3-chat");
        r.ImageDeploymentName.Should().Be("gpt-image-2");
        r.TranscribeDeploymentName.Should().Be("gpt-4o-transcribe");
        r.ImageEndpoint.Should().Be("https://image-host/");
        r.TranscribeEndpoint.Should().Be("https://transcribe-host/");
    }
}
