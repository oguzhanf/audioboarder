using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Services.Layout;

namespace AudioBoarder.Tests.Layout;

public class MindMapLayoutEngineTests
{
    private readonly MindMapLayoutEngine _engine = new();
    private readonly ScenePatchApplier _applier = new();

    private static double Dist(SceneNode a, SceneNode b)
        => Math.Sqrt(Math.Pow(a.X!.Value - b.X!.Value, 2) + Math.Pow(a.Y!.Value - b.Y!.Value, 2));

    [Fact]
    public void PositionsAllNodes_AroundCentralHub()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("hub", NodeKind.Process, "Central idea"),
            new AddNode("b1", NodeKind.Entity, "Branch 1"),
            new AddNode("b2", NodeKind.Entity, "Branch 2"),
            new AddNode("b3", NodeKind.Entity, "Branch 3"),
            new AddNode("b4", NodeKind.Entity, "Branch 4"),
            new Connect("e1", "hub", "b1"),
            new Connect("e2", "hub", "b2"),
            new Connect("e3", "hub", "b3"),
            new Connect("e4", "hub", "b4"),
        }));

        var result = _engine.Apply(graph, new LayoutOptions());

        result.NodesPositioned.Should().Be(5);
        graph.Nodes.Values.Should().OnlyContain(n => n.X.HasValue && n.Y.HasValue);

        // The hub is the most-connected node, so every branch sits at a similar
        // radius around it (radial), not stacked in one vertical column.
        var hub = graph.Nodes["hub"];
        var radii = new[] { "b1", "b2", "b3", "b4" }.Select(id => Dist(graph.Nodes[id], hub)).ToList();
        radii.Should().OnlyContain(r => r > 100);
        (radii.Max() - radii.Min()).Should().BeLessThan(5); // all on the same ring
    }

    [Fact]
    public void CentralHubIsEnlarged()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("hub", NodeKind.Process, "Central idea"),
            new AddNode("b1", NodeKind.Entity, "Branch 1"),
            new AddNode("b2", NodeKind.Entity, "Branch 2"),
            new Connect("e1", "hub", "b1"),
            new Connect("e2", "hub", "b2"),
        }));

        _engine.Apply(graph, new LayoutOptions());

        graph.Nodes["hub"].Width.Should().BeGreaterThan(graph.Nodes["b1"].Width);
    }

    [Fact]
    public void SeparateComponents_DoNotOverlap_AndStackIn2D()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            // Two unrelated mind maps (two central ideas).
            new AddNode("a", NodeKind.Process, "Idea A"),
            new AddNode("a1", NodeKind.Entity, "A child"),
            new Connect("ea", "a", "a1"),
            new AddNode("b", NodeKind.Process, "Idea B"),
            new AddNode("b1", NodeKind.Entity, "B child"),
            new Connect("eb", "b", "b1"),
        }));

        var result = _engine.Apply(graph, new LayoutOptions());

        result.NodesPositioned.Should().Be(4);
        // The two centres must be clearly separated (different cells in the grid).
        Dist(graph.Nodes["a"], graph.Nodes["b"]).Should().BeGreaterThan(150);
    }

    [Fact]
    public void LockedNodes_KeepPosition()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("hub", NodeKind.Process, "Central"),
            new AddNode("b1", NodeKind.Entity, "Branch"),
            new Connect("e1", "hub", "b1"),
        }));
        graph.Nodes["hub"].X = 999;
        graph.Nodes["hub"].Y = 888;
        graph.Nodes["hub"].Locked = true;

        _engine.Apply(graph, new LayoutOptions());

        graph.Nodes["hub"].X.Should().Be(999);
        graph.Nodes["hub"].Y.Should().Be(888);
        graph.Nodes["b1"].X.Should().NotBeNull();
    }

    [Fact]
    public void IsolatedNode_IsPositioned()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("lonely", NodeKind.Note, "Standalone"),
        }));

        var result = _engine.Apply(graph, new LayoutOptions());

        result.NodesPositioned.Should().Be(1);
        graph.Nodes["lonely"].X.Should().NotBeNull();
        graph.Nodes["lonely"].Y.Should().NotBeNull();
    }

    [Fact]
    public void CoordinatesArePositiveAndFinite()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("hub", NodeKind.Process, "Central"),
            new AddNode("b1", NodeKind.Entity, "B1"),
            new AddNode("b2", NodeKind.Entity, "B2"),
            new AddNode("c1", NodeKind.Entity, "C1"),
            new Connect("e1", "hub", "b1"),
            new Connect("e2", "hub", "b2"),
            new Connect("e3", "b1", "c1"),
        }));

        _engine.Apply(graph, new LayoutOptions());

        foreach (var n in graph.Nodes.Values)
        {
            double.IsFinite(n.X!.Value).Should().BeTrue();
            double.IsFinite(n.Y!.Value).Should().BeTrue();
            n.X!.Value.Should().BeGreaterThanOrEqualTo(0);
            n.Y!.Value.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public void DeeperLevelsSitFartherFromHub()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("hub", NodeKind.Process, "Central"),
            new AddNode("b1", NodeKind.Entity, "Branch"),
            new AddNode("c1", NodeKind.Entity, "Sub-idea"),
            new Connect("e1", "hub", "b1"),
            new Connect("e2", "b1", "c1"),
        }));

        _engine.Apply(graph, new LayoutOptions());

        var hub = graph.Nodes["hub"];
        Dist(graph.Nodes["c1"], hub).Should().BeGreaterThan(Dist(graph.Nodes["b1"], hub));
    }

    [Fact]
    public void EmptyGraph_ReturnsZeroResult()
    {
        var result = _engine.Apply(new SceneGraph(), new LayoutOptions());
        result.NodesPositioned.Should().Be(0);
    }
}
