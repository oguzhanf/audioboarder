using System.Text.Json;
using AudioBoarder.Core.Excalidraw;
using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;

namespace AudioBoarder.Tests.Excalidraw;

public class SceneToCanvasJsonTests
{
    private readonly ScenePatchApplier _applier = new();

    [Fact]
    public void EmitsAuthoritativeGeometryIntentLifecycleAndMetadata()
    {
        var graph = BuildScene();
        graph.SetIntentState(new DiagramIntentState(
            DiagramIntent.IntegrationDataFlowArchitecture,
            DiagramIntentSelectionMode.PinnedByUser, 1, "test", graph.Revision));
        graph.TryUpdateNodeGeometry("api", 420, 210, 190, 80, locked: true);

        using var json = JsonDocument.Parse(SceneToCanvasJson.Serialize(graph, graph.Revision));
        var root = json.RootElement;
        root.GetProperty("intent").GetString().Should().Be("integration_data_flow_architecture");

        var api = root.GetProperty("nodes").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "api");
        api.GetProperty("centerX").GetDouble().Should().Be(420);
        api.GetProperty("centerY").GetDouble().Should().Be(210);
        api.GetProperty("width").GetDouble().Should().Be(190);
        api.GetProperty("height").GetDouble().Should().Be(80);
        api.GetProperty("locked").GetBoolean().Should().BeTrue();
        api.GetProperty("lifecycle").GetString().Should().Be("user_edited");

        var edge = root.GetProperty("edges").EnumerateArray().Single();
        edge.GetProperty("step").GetInt32().Should().Be(2);
        edge.GetProperty("label").GetString().Should().Be("Create order");
        edge.GetProperty("protocol").GetString().Should().Be("HTTPS");
        edge.GetProperty("payload").GetString().Should().Be("JSON");
        edge.GetProperty("authentication").GetString().Should().Be("OAuth");
        edge.GetProperty("dataClassification").GetString().Should().Be("Confidential");
        edge.GetProperty("interactionMode").GetString().Should().Be("synchronous");
        edge.GetProperty("lifecycle").GetString().Should().Be("provisional");

        var groups = root.GetProperty("groups").EnumerateArray().ToArray();
        groups.Should().HaveCount(2);
        groups.Should().OnlyContain(g =>
            g.GetProperty("width").GetDouble() > 0 &&
            g.GetProperty("height").GetDouble() > 0);
        var outer = groups.Single(g => g.GetProperty("id").GetString() == "outer");
        outer.GetProperty("boundaryKind").GetString().Should().Be("cloud_scope");
        outer.GetProperty("subtitle").GetString().Should().Be("West Europe");
        outer.GetProperty("lifecycle").GetString().Should().Be("provisional");
    }

    [Fact]
    public void CanvasAndExcalidrawExportsHaveSemanticAndGeometryParity()
    {
        var graph = BuildScene();
        using var canvas = JsonDocument.Parse(SceneToCanvasJson.Serialize(graph, graph.Revision));
        var export = new SceneToExcalidrawConverter().Convert(graph);
        var snapshot = LayoutSnapshot.Capture(graph);

        canvas.RootElement.GetProperty("nodes").GetArrayLength().Should().Be(graph.Nodes.Count);
        canvas.RootElement.GetProperty("edges").GetArrayLength().Should().Be(graph.Edges.Count);
        canvas.RootElement.GetProperty("groups").GetArrayLength().Should().Be(graph.Groups.Count);
        export.Elements.Should().Contain(x => x.Id == "outer_frame",
            "outer groups containing only child groups must export");
        export.Elements.Single(x => x.Id == "inner_frame").FrameId.Should().Be("outer_frame");

        var api = export.Elements.Single(x => x.Id == "api");
        api.X.Should().Be(snapshot.Nodes["api"].Left);
        api.Y.Should().Be(snapshot.Nodes["api"].Top);
        api.Width.Should().Be(snapshot.Nodes["api"].Width);
        api.Height.Should().Be(snapshot.Nodes["api"].Height);

        var edgeText = export.Elements.Single(x => x.Id == "request_label").Text;
        edgeText.Should().Contain("Step 2").And.Contain("Create order")
            .And.Contain("HTTPS").And.Contain("OAuth").And.Contain("Confidential");
        export.Elements.Single(x => x.Id == "inner_frame").Name.Should()
            .Contain("Network").And.Contain("Application subnet");
    }

    [Fact]
    public void UnlockedUserEditedNodeStaysUnlockedAcrossHostSerializationAndExport()
    {
        var graph = BuildScene();
        graph.TryUpdateNodeGeometry("api", 420, 210, 190, 80, locked: true);
        graph.TryUpdateNodeGeometry("api", 420, 210, 190, 80, locked: false);
        graph.Nodes["api"].LifecycleState.Should().Be(ElementLifecycleState.UserEdited);
        graph.Nodes["api"].Locked.Should().BeFalse();

        using var canvas = JsonDocument.Parse(SceneToCanvasJson.Serialize(graph, graph.Revision));
        var apiPayload = canvas.RootElement.GetProperty("nodes").EnumerateArray()
            .Single(x => x.GetProperty("id").GetString() == "api");
        apiPayload.TryGetProperty("locked", out _).Should().BeFalse(
            "semantic user-edit protection must not repin geometry in the host");

        var exported = new SceneToExcalidrawConverter().Convert(graph);
        exported.Elements.Single(x => x.Id == "api").Locked.Should().BeFalse();
    }

    private SceneGraph BuildScene()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("client", NodeKind.Actor, "Client"),
            new AddNode("api", NodeKind.Process, "API"),
            new Connect("request", "client", "api", EdgeKind.Flow, "Create order", 2,
                "HTTPS", "JSON", "Confidential", "OAuth", InteractionMode.Synchronous),
            new GroupOp("outer", "Production", Array.Empty<string>(), null, "West Europe",
                BoundaryKind.CloudScope),
            new GroupOp("inner", "Network", new[] { "api" }, "outer", "Application subnet",
                BoundaryKind.Network),
        }));
        graph.Nodes["client"].X = 120;
        graph.Nodes["client"].Y = 200;
        graph.Nodes["client"].Width = 160;
        graph.Nodes["client"].Height = 64;
        graph.Nodes["api"].X = 420;
        graph.Nodes["api"].Y = 200;
        graph.Nodes["api"].Width = 180;
        graph.Nodes["api"].Height = 72;
        return graph;
    }
}
