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
}
