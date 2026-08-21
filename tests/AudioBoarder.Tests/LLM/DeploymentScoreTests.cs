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
}
