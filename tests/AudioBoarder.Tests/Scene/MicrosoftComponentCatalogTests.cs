using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Scene;

public sealed class MicrosoftComponentCatalogTests
{
    [Theory]
    [InlineData("private link", "private-endpoint")]
    [InlineData("sharepoint", "sharepoint")]
    [InlineData("domain controller", "active-directory-ds")]
    [InlineData("power automate", "power-automate")]
    public void SearchCoversCloudAndOnPremisesAliases(string query, string expectedId)
    {
        MicrosoftComponentCatalog.Search(query)
            .Should().Contain(x => x.Id == expectedId);
    }

    [Fact]
    public void CanvasPayloadReferencesArchitectureCenterAndContainsCatalog()
    {
        var json = MicrosoftComponentCatalog.ToCanvasJson();

        json.Should().Contain("\"type\":\"component-library\"");
        json.Should().Contain(MicrosoftComponentCatalog.SourceUrl);
        json.Should().Contain("\"azure-openai\"");
    }

    [Theory]
    [InlineData("a cache and a queue")]
    [InlineData("Kubernetes and PostgreSQL")]
    [InlineData("Redis stores temporary values")]
    [InlineData("The web app calls an API gateway")]
    [InlineData("A new workflow for our teams")]
    [InlineData("Machine learning and abstract functions")]
    [InlineData("NGINX application gateway")]
    [InlineData("A Cloudflare front door")]
    public void DiscoveryAliasesAreNotEvidenceOfMicrosoftProducts(string discussion)
    {
        MicrosoftComponentCatalog.RelevantPromptVocabulary([discussion]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Azure Front Door sends requests to App Service.", "Azure Front Door")]
    [InlineData("We use AKS and Azure SQL Database.", "Azure Kubernetes Service")]
    [InlineData("Microsoft Teams works with Power BI.", "Power BI")]
    [InlineData("Active Directory domain controllers remain on premises.", "Active Directory Domain Services")]
    public void NamedMicrosoftProductsRetainTheirExactVocabulary(string discussion, string expected)
    {
        MicrosoftComponentCatalog.RelevantPromptVocabulary([discussion]).Should().Contain(expected);
    }
}
