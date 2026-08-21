using AudioBoarder.Core.Layout;
using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Services.Layout;

namespace AudioBoarder.Tests.Layout;

public class MsaglLayoutEngineTests
{
    private readonly MsaglLayoutEngine _engine = new();
    private readonly ScenePatchApplier _applier = new();

    [Fact]
    public void PositionsAllNodes_OnLinearChain_TopToBottom()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A"),
            new AddNode("b", NodeKind.Process, "B"),
            new AddNode("c", NodeKind.Process, "C"),
            new Connect("e1", "a", "b"),
            new Connect("e2", "b", "c"),
        }));

        var result = _engine.Apply(graph, new LayoutOptions());

        result.NodesPositioned.Should().Be(3);
        graph.Nodes.Values.Should().OnlyContain(n => n.X.HasValue && n.Y.HasValue);
        graph.Nodes["a"].Y!.Value.Should().BeLessThan(graph.Nodes["b"].Y!.Value);
        graph.Nodes["b"].Y!.Value.Should().BeLessThan(graph.Nodes["c"].Y!.Value);
    }

    [Fact]
    public void LockedNodes_KeepPosition()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A"),
            new AddNode("b", NodeKind.Process, "B"),
            new Connect("e1", "a", "b"),
        }));
        graph.Nodes["a"].X = 555;
        graph.Nodes["a"].Y = 777;
        graph.Nodes["a"].Locked = true;

        _engine.Apply(graph, new LayoutOptions());

        graph.Nodes["a"].X.Should().Be(555);
        graph.Nodes["a"].Y.Should().Be(777);
        graph.Nodes["b"].X.Should().NotBeNull();
        graph.Nodes["b"].Y.Should().NotBeNull();
    }

    [Fact]
    public void HandlesCyclesWithoutThrowing()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A"),
            new AddNode("b", NodeKind.Process, "B"),
            new Connect("e1", "a", "b"),
            new Connect("e2", "b", "a"),
        }));

        var result = _engine.Apply(graph, new LayoutOptions());

        result.NodesPositioned.Should().Be(2);
        graph.Nodes.Values.Should().OnlyContain(n => n.X.HasValue && n.Y.HasValue);
    }

    [Fact]
    public void PositionsDisconnectedNodes()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A"),
            new AddNode("b", NodeKind.Entity, "B"),
            new AddNode("c", NodeKind.DataStore, "C"),
        }));

        var result = _engine.Apply(graph, new LayoutOptions());

        result.NodesPositioned.Should().Be(3);
        graph.Nodes.Values.Should().OnlyContain(n => n.X.HasValue && n.Y.HasValue);
    }

    [Fact]
    public void CoordinatesArePositiveAndFinite()
    {
        var graph = new SceneGraph();
        _applier.Apply(graph, new ScenePatch(new ScenePatchOperation[]
        {
            new AddNode("a", NodeKind.Process, "A"),
            new AddNode("b", NodeKind.Process, "B"),
            new AddNode("c", NodeKind.Process, "C"),
            new Connect("e1", "a", "b"),
            new Connect("e2", "a", "c"),
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
    public void EmptyGraph_ReturnsZeroResult()
    {
        var result = _engine.Apply(new SceneGraph(), new LayoutOptions());
        result.NodesPositioned.Should().Be(0);
    }
}
