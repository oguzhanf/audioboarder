using System.Text.Json;
using System.Xml.Linq;
using AudioBoarder.Core.Excalidraw;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Scene;

public sealed class ComponentIconVisualsTests
{
    [Theory]
    [InlineData("azure-front-door")]
    [InlineData("application-gateway")]
    [InlineData("load-balancer")]
    [InlineData("azure-firewall")]
    [InlineData("azure-openai")]
    public void AzureComponentsHaveRealBundledArtworkWithoutAnyUserSetup(string id)
    {
        var component = MicrosoftComponentCatalog.Find(id)!;
        var visual = ComponentIconVisuals.ForComponent(component);
        visual.IsOfficial.Should().BeTrue();
        XDocument.Parse(visual.Svg).Root!.Name.LocalName.Should().Be("svg");
        visual.Svg.Should().NotBe(IconRegistry.RenderSvg("box", "#0078d4", 32));
    }

    [Fact]
    public void EveryLibraryEntryHasAVisualAndProductIconsAreDistinct()
    {
        using var document = JsonDocument.Parse(MicrosoftComponentCatalog.ToCanvasJson());
        var entries = document.RootElement.GetProperty("components").EnumerateArray().ToArray();
        entries.Should().OnlyContain(entry => entry.GetProperty("svg").GetString()!.Contains("<svg"));
        entries.Count(entry => entry.GetProperty("iconIsOfficial").GetBoolean()).Should().BeGreaterThan(40);
        var door = entries.Single(e => e.GetProperty("id").GetString() == "azure-front-door");
        var gateway = entries.Single(e => e.GetProperty("id").GetString() == "application-gateway");
        door.GetProperty("svg").GetString().Should().NotBe(gateway.GetProperty("svg").GetString());
    }

    [Fact]
    public void NodeBridgeUsesTheSameArtworkAsTheLibrary()
    {
        var definition = MicrosoftComponentCatalog.Find("azure-front-door")!;
        var graph = new SceneGraph();
        graph.TryAddUserNode(new SceneNode
        {
            Id = "front-door", Label = definition.Name, Kind = definition.Kind,
            X = 200, Y = 100,
        }).Should().BeTrue();

        using var scene = JsonDocument.Parse(SceneToCanvasJson.Serialize(graph, graph.Revision));
        scene.RootElement.GetProperty("nodes")[0].GetProperty("svg").GetString()
            .Should().Be(ComponentIconVisuals.ForComponent(definition).Svg);
    }

    [Fact]
    public void NonAzureComponentsStillHaveMeaningfulVectorIcons()
    {
        var component = MicrosoftComponentCatalog.Find("power-bi")!;
        var visual = ComponentIconVisuals.ForComponent(component);
        visual.IsOfficial.Should().BeFalse();
        visual.Svg.Should().Be(IconRegistry.RenderSvg("bar-chart", "#0078d4", 32));
    }

    [Fact]
    public void PreviouslyDroppedUndersizedCardsAreRepairedWithoutMovingThem()
    {
        var component = MicrosoftComponentCatalog.Find("application-gateway")!;
        var scene = new SceneGraph();
        var node = new SceneNode
        {
            Id = "user-application-gateway-example", Label = component.Name,
            Description = component.Description, X = 100, Y = 120, Width = 190, Height = 70,
        };
        scene.TryAddUserNode(node).Should().BeTrue();
        MicrosoftComponentCatalog.RepairLegacyDropSizes(scene).Should().Be(1);
        node.X.Should().Be(100);
        node.Y.Should().Be(120);
        node.Width.Should().BeGreaterThanOrEqualTo(260);
        node.Height.Should().BeGreaterThanOrEqualTo(104);
        MicrosoftComponentCatalog.RepairLegacyDropSizes(scene).Should().Be(0);
    }
}
