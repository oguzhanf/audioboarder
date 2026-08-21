using AudioBoarder.Core.Patch;
using AudioBoarder.Core.Scene;
using AudioBoarder.Core.Layout;
using AudioBoarder.Services.Layout;

namespace AudioBoarder.Tests.Layout;

public class LayeredLayoutEngineTests
{
    private readonly LayeredLayoutEngine _engine = new();
    private readonly ScenePatchApplier _applier = new();

    [Fact]
    public void PositionsAllNodes_OnLinearChain()
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
        // chain should be laid out top-to-bottom: a above b above c
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
    }

    [Fact]
    public void HandlesCyclesWithoutInfiniteLoop()
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
    }
}
